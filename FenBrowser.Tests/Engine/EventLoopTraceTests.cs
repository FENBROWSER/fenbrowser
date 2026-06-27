using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Engine;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Engine;

[Collection(EngineLogTestCollection.Name)]
public sealed class EventLoopTraceTests
{
    [Fact]
    public void Coordinator_WritesTaskMicrotaskTimerRafAndRenderTrace()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-event-loop-trace-{Guid.NewGuid():N}.jsonl");
        try
        {
            ConfigureTrace(tracePath);
            EnginePhaseManager.EnterPhase(EnginePhase.Idle);

            var loop = EventLoopCoordinator.CreateIsolated();
            loop.ScheduleTask(
                () => loop.ScheduleMicrotask(() => { }),
                TaskSource.DOMManipulation,
                "dom-task");
            loop.ScheduleDelayedTask(() => { }, 1, TaskSource.Timer, "delayed-timer");
            loop.ScheduleAnimationFrame(() => loop.ScheduleMicrotask(() => { }));
            loop.SetRenderCallback(() => { });
            loop.NotifyLayoutDirty();

            Thread.Sleep(20);
            loop.ProcessNextTaskDetailed();
            loop.ProcessNextTaskDetailed();

            DisableTrace();

            var events = LoadEventLoopTraceEvents(tracePath);
            Assert.Contains(events, e => e.Event == "TaskQueued" && e.TaskId.StartsWith("task-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "TaskStarted" && e.Data.GetProperty("description").GetString() == "dom-task");
            Assert.Contains(events, e => e.Event == "TaskCompleted" && e.Data.GetProperty("success").GetBoolean());
            Assert.Contains(events, e => e.Event == "MicrotaskQueued" && e.TaskId.StartsWith("microtask-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "MicrotaskCheckpointStarted");
            Assert.Contains(events, e => e.Event == "MicrotaskExecuted" && e.TaskId.StartsWith("microtask-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "MicrotaskCheckpointCompleted");
            Assert.Contains(events, e => e.Event == "TimerScheduled" && e.TaskId.StartsWith("timer-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "TimerFired" && e.TaskId.StartsWith("timer-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "RequestAnimationFrameScheduled" && e.TaskId.StartsWith("raf-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "RequestAnimationFrameFired" && e.TaskId.StartsWith("raf-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "RenderOpportunityStarted");
            Assert.Contains(events, e => e.Event == "RenderOpportunityCompleted" && e.Data.GetProperty("renderRan").GetBoolean());
        }
        finally
        {
            DisableTrace();
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    [Fact]
    public async Task FenJsBrowserTimers_WriteTaskTimerRafAndMicrotaskTrace()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-fenjs-event-loop-trace-{Guid.NewGuid():N}.jsonl");
        var baseUri = new Uri("https://example.test/event-loop.html");
        try
        {
            ConfigureTrace(tracePath);
            BrowserScriptEngineRuntime.Reset();

            var document = new HtmlParser(
                "<html><body><script>" +
                "setTimeout(function(){globalThis.__timerDone=true;Promise.resolve().then(function(){globalThis.__timerMicrotask=true;});},1);" +
                "requestAnimationFrame(function(){globalThis.__rafDone=true;});" +
                "</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var completed = SpinWait.SpinUntil(
                () => Equals(true, engine.Evaluate("Boolean(globalThis.__timerDone)&&Boolean(globalThis.__rafDone)&&Boolean(globalThis.__timerMicrotask)")),
                millisecondsTimeout: 1000);
            Assert.True(completed);

            DisableTrace();

            var events = LoadEventLoopTraceEvents(tracePath);
            Assert.Contains(events, e => e.Event == "TimerScheduled" && e.TaskId.StartsWith("timer-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "TimerFired" && e.TaskId.StartsWith("timer-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "RequestAnimationFrameScheduled" && e.TaskId.StartsWith("raf-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "RequestAnimationFrameFired" && e.TaskId.StartsWith("raf-", StringComparison.Ordinal));
            Assert.Contains(events, e => e.Event == "TaskStarted" && !string.IsNullOrWhiteSpace(e.TaskId));
            Assert.Contains(events, e => e.Event == "TaskCompleted" && e.Data.GetProperty("success").GetBoolean());
            Assert.Contains(events, e => e.Event == "MicrotaskCheckpointCompleted");
        }
        finally
        {
            DisableTrace();
            BrowserScriptEngineRuntime.Reset();
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    private static void ConfigureTrace(string tracePath)
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
    }

    private static void DisableTrace()
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
    }

    private static List<EventLoopTraceEvent> LoadEventLoopTraceEvents(string tracePath)
    {
        var events = new List<EventLoopTraceEvent>();
        foreach (var line in File.ReadAllLines(tracePath))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("category").GetString() != "EventLoop")
            {
                continue;
            }

            var taskId = root.GetProperty("task_id").ValueKind == JsonValueKind.String
                ? root.GetProperty("task_id").GetString()
                : string.Empty;
            events.Add(new EventLoopTraceEvent(
                root.GetProperty("event").GetString(),
                taskId ?? string.Empty,
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

    private sealed record EventLoopTraceEvent(string Event, string TaskId, JsonElement Data);
}
