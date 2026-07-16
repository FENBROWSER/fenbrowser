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

    [Fact]
    public async Task ThrowingEventListener_PreservesTypedFailureProvenance()
    {
        var baseUri = new Uri("https://fixture.test/throwing-listener.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><button id='target'>target</button><script>" +
                "document.getElementById('target').addEventListener('click'," +
                "function clickFixtureReceiver(){throw new Error('listener-fixture-boom');});" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.True(engine.DispatchEventForElement(document.GetElementById("target"), "click"));

            var failure = Assert.Single(engine.GetEventLoopSnapshot().CallbackFailureRecords);
            Assert.Equal("event-listener", failure.CallbackCategory);
            Assert.Equal("click", failure.EventType);
            Assert.StartsWith("event-", failure.TaskId);
            Assert.Equal("clickFixtureReceiver", failure.CallbackFunctionName);
            Assert.Equal("script-1", failure.ScriptId);
            Assert.Equal("inline#1", failure.ScriptSourceLabel);
            Assert.Equal("HostObject", failure.ReceiverJsType);
            Assert.False(string.IsNullOrWhiteSpace(failure.ReceiverHostType));
            Assert.Contains("listener-fixture-boom", failure.ExceptionMessage);
            Assert.Contains("clickFixtureReceiver", failure.JsStack);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task RejectedPromise_PreservesTypedFailureProvenance()
    {
        var baseUri = new Uri("https://fixture.test/rejected-promise.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "Promise.reject(new Error('promise-fixture-boom'));" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var failure = Assert.Single(engine.GetEventLoopSnapshot().CallbackFailureRecords);
            Assert.Equal("promise-rejection", failure.CallbackCategory);
            Assert.Equal("reject", failure.EventType);
            Assert.StartsWith("promise-", failure.TaskId);
            Assert.Equal("script-1", failure.ScriptId);
            Assert.Equal("inline#1", failure.ScriptSourceLabel);
            Assert.Equal("Object", failure.ReceiverJsType);
            Assert.Contains("promise-fixture-boom", failure.ExceptionMessage);
            Assert.Contains("promise-fixture-boom", failure.JsStack);
            Assert.False(string.IsNullOrWhiteSpace(failure.HostStack));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task RejectionHandledBeforeCheckpoint_IsNotReportedAsFailure()
    {
        var baseUri = new Uri("https://fixture.test/handled-promise.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "Promise.reject(new Error('handled-promise')).catch(function () {});" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Empty(engine.GetEventLoopSnapshot().CallbackFailureRecords);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task MultiplePromiseRejections_PreserveObservationOrder()
    {
        var baseUri = new Uri("https://fixture.test/ordered-rejections.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "Promise.reject(new Error('first-rejection'));" +
                "Promise.reject(new Error('second-rejection'));" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var failures = engine.GetEventLoopSnapshot().CallbackFailureRecords;
            Assert.Equal(2, failures.Count);
            Assert.Contains("first-rejection", failures[0].ExceptionMessage);
            Assert.Contains("second-rejection", failures[1].ExceptionMessage);
            Assert.True(failures[0].Sequence < failures[1].Sequence);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task RepeatingTimerFailures_PreserveTimerIdentityAndOrder()
    {
        var baseUri = new Uri("https://fixture.test/repeating-timer.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "globalThis.__repeatCount=0;" +
                "globalThis.__intervalId=setInterval(function repeatingTimerFixture(){" +
                "globalThis.__repeatCount++;" +
                "if(globalThis.__repeatCount>=2){clearInterval(globalThis.__intervalId);}" +
                "throw new Error('repeating-timer-'+globalThis.__repeatCount);" +
                "},1);" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.True(SpinWait.SpinUntil(
                () => engine.GetEventLoopSnapshot().CallbackFailures >= 2,
                millisecondsTimeout: 1000));

            var snapshot = engine.GetEventLoopSnapshot();
            Assert.Equal(2, snapshot.CallbackFailures);
            Assert.Collection(
                snapshot.CallbackFailureRecords,
                failure => Assert.Contains("repeating-timer-1", failure.ExceptionMessage),
                failure => Assert.Contains("repeating-timer-2", failure.ExceptionMessage));
            Assert.All(snapshot.CallbackFailureRecords, failure =>
            {
                Assert.Equal("setInterval", failure.CallbackCategory);
                Assert.Equal("repeatingTimerFixture", failure.CallbackFunctionName);
                Assert.NotNull(failure.TimerId);
            });
            Assert.Equal(
                snapshot.CallbackFailureRecords[0].TimerId,
                snapshot.CallbackFailureRecords[1].TimerId);
            Assert.True(
                snapshot.CallbackFailureRecords[0].Sequence <
                snapshot.CallbackFailureRecords[1].Sequence);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task CallbackFailureRecords_AreBoundedWithoutLosingTotalCount()
    {
        var baseUri = new Uri("https://fixture.test/bounded-callback-failures.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "for(var i=0;i<140;i++){" +
                "setTimeout(function(index){throw new Error('bounded-timer-'+index);},1,i);" +
                "}" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.True(SpinWait.SpinUntil(
                () => engine.GetEventLoopSnapshot().CallbackFailures >= 140,
                millisecondsTimeout: 2000));

            var snapshot = engine.GetEventLoopSnapshot();
            Assert.Equal(140, snapshot.CallbackFailures);
            Assert.Equal(128, snapshot.CallbackFailureRecords.Count);
            Assert.Equal(13, snapshot.CallbackFailureRecords[0].Sequence);
            Assert.Equal(140, snapshot.CallbackFailureRecords[^1].Sequence);
            Assert.All(
                snapshot.CallbackFailureRecords,
                failure => Assert.Equal("setTimeout", failure.CallbackCategory));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task SecretLikeCallbackFailureText_IsRedacted()
    {
        var baseUri = new Uri("https://fixture.test/redacted-callback.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "setTimeout(function redactedTimerFixture(){" +
                "throw new Error('authorization: Basic secret-fixture-value');" +
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
            Assert.Equal("[redacted secret-like diagnostic line]", failure.ExceptionMessage);
            Assert.DoesNotContain("secret-fixture-value", failure.JsStack, StringComparison.Ordinal);
            Assert.Contains("redactedTimerFixture", failure.JsStack, StringComparison.Ordinal);
            Assert.Equal(
                "metadata-only; secret-like stack lines redacted",
                failure.RedactionStatus);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task CallbackFailureMessageAndStacks_AreBounded()
    {
        var baseUri = new Uri("https://fixture.test/bounded-callback-text.html");
        var longMessage = new string('x', 10_000);
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "setTimeout(function boundedTextTimerFixture(){" +
                "throw new Error('" + longMessage + "');" +
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
            Assert.Equal(2_051, failure.ExceptionMessage.Length);
            Assert.Equal(8_195, failure.JsStack.Length);
            Assert.True(failure.HostStack.Length <= 8_195);
            Assert.EndsWith("...", failure.ExceptionMessage, StringComparison.Ordinal);
            Assert.EndsWith("...", failure.JsStack, StringComparison.Ordinal);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task ThrowingMicrotask_PreservesItsOwnCallbackProvenance()
    {
        var baseUri = new Uri("https://fixture.test/throwing-microtask.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser(
                "<html><body><script>" +
                "setTimeout(function parentTimerFixture(){" +
                "queueMicrotask(function microtaskFixtureReceiver(){" +
                "throw new Error('microtask-fixture-boom');" +
                "});" +
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
            Assert.Equal("microtask", failure.CallbackCategory);
            Assert.StartsWith("microtask-", failure.TaskId, StringComparison.Ordinal);
            Assert.Equal("microtaskFixtureReceiver", failure.CallbackFunctionName);
            Assert.Equal("script-1", failure.ScriptId);
            Assert.Equal("inline#1", failure.ScriptSourceLabel);
            Assert.Equal("Undefined", failure.ReceiverJsType);
            Assert.Contains("microtask-fixture-boom", failure.ExceptionMessage);
            Assert.Contains("microtaskFixtureReceiver", failure.JsStack);
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
