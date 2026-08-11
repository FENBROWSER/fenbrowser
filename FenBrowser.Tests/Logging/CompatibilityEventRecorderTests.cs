using FenBrowser.Core.Logging;

namespace FenBrowser.Tests.Logging;

[Collection(EngineLogTestCollection.Name)]
public sealed class CompatibilityEventRecorderTests
{
    [Fact]
    public void EngineEvent_IsNormalizedWithCorrelationAndContext()
    {
        CompatibilityEventRecorder.Clear();
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var evt = CreateEvent(
            eventId,
            LogSubsystem.Event,
            new EngineLogContext(
                NavigationId: "nav-7",
                FrameId: "frame-2",
                DocumentId: "doc-4",
                RealmId: "realm-3",
                RequestId: "request-6",
                TaskId: "task-8"),
            correlationId,
            new Dictionary<string, object> { ["eventName"] = "TaskFailed" });

        CompatibilityEventRecorder.Record(evt);

        var captured = Assert.Single(CompatibilityEventRecorder.Snapshot());
        Assert.Equal(CompatibilityEventEnvelope.CurrentSchema, captured.Schema);
        Assert.Equal("event", captured.Domain);
        Assert.Equal("TaskFailed", captured.Kind);
        Assert.Equal("failure", captured.Outcome);
        Assert.Equal(correlationId.ToString("N"), captured.CorrelationId);
        Assert.Equal("nav-7", captured.NavigationId);
        Assert.Equal("frame-2", captured.FrameId);
        Assert.Equal("doc-4", captured.DocumentId);
        Assert.Equal("realm-3", captured.RealmId);
        Assert.Equal("request-6", captured.RequestId);
        Assert.Equal("task-8", captured.TaskId);
    }

    [Fact]
    public void DuplicateEventId_IsRecordedOnce()
    {
        CompatibilityEventRecorder.Clear();
        var evt = CreateEvent(Guid.NewGuid(), LogSubsystem.Ipc, default, null,
            new Dictionary<string, object> { ["eventKind"] = "ipc.ack" });

        CompatibilityEventRecorder.Record(evt);
        CompatibilityEventRecorder.Record(evt);

        var captured = Assert.Single(CompatibilityEventRecorder.Snapshot());
        Assert.Equal("ipc", captured.Domain);
        Assert.Equal("ipc.ack", captured.Kind);
    }

    [Fact]
    public void ExistingDiagnosticKinds_AreClassifiedWithoutParallelAdapters()
    {
        var missing = CompatibilityEventRecorder.Normalize(CreateEvent(
            Guid.NewGuid(),
            LogSubsystem.Js,
            default,
            null,
            new Dictionary<string, object>
            {
                ["eventName"] = "MissingApiObserved",
                ["traceCategory"] = "MissingAPI",
                ["apiName"] = "Navigator.prototype.example"
            }));
        var network = CompatibilityEventRecorder.Normalize(CreateEvent(
            Guid.NewGuid(), LogSubsystem.Fetch, default, null,
            new Dictionary<string, object> { ["eventName"] = "ResponseReceived", ["requestId"] = "request-1" }));
        var mutation = CompatibilityEventRecorder.Normalize(CreateEvent(
            Guid.NewGuid(), LogSubsystem.Dom, default, null,
            new Dictionary<string, object> { ["eventName"] = "MutationApplied" }));

        Assert.Equal("missing-api", missing.Domain);
        Assert.Equal("compatibility-gap", missing.Outcome);
        Assert.Equal("network", network.Domain);
        Assert.Equal("request-1", network.RequestId);
        Assert.Equal("dom", mutation.Domain);
    }

    [Theory]
    [InlineData(WebIdlBehaviorKind.ReturnType, "return-type")]
    [InlineData(WebIdlBehaviorKind.Descriptor, "descriptor")]
    [InlineData(WebIdlBehaviorKind.Semantics, "semantics")]
    public void WebIdlBehaviorMismatch_EmitsTypedExpectedActualEvidence(
        WebIdlBehaviorKind behavior,
        string expectedBehavior)
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Trace,
            EnableRingBufferSink = true
        });
        EngineLog.ClearCompatibilityBuffer();

        CompatibilityEventRecorder.RecordWebIdlBehaviorMismatch(
            "HTMLInputElement",
            "value",
            behavior,
            "string",
            "undefined",
            new EngineLogContext(
                NavigationId: "nav-webidl",
                DocumentId: "doc-webidl",
                RealmId: "realm-webidl"));

        var captured = Assert.Single(CompatibilityEventRecorder.Snapshot());
        Assert.Equal("webidl", captured.Domain);
        Assert.Equal("webidl.behavior-mismatch", captured.Kind);
        Assert.Equal("compatibility-gap", captured.Outcome);
        Assert.Equal("nav-webidl", captured.NavigationId);
        Assert.Equal("doc-webidl", captured.DocumentId);
        Assert.Equal("realm-webidl", captured.RealmId);
        Assert.Equal(expectedBehavior, captured.Fields["behavior"]);
        Assert.Equal("string", captured.Fields["expected"]);
        Assert.Equal("undefined", captured.Fields["actual"]);
    }

    private static EngineLogEvent CreateEvent(
        Guid eventId,
        LogSubsystem subsystem,
        EngineLogContext context,
        Guid? correlationId,
        IReadOnlyDictionary<string, object> fields)
    {
        return new EngineLogEvent(
            new EngineLogHeader(
                DateTimeOffset.UtcNow,
                1,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                subsystem,
                LogSeverity.Warn,
                LogMarker.None,
                eventId,
                null,
                correlationId,
                context),
            new EngineLogPayload
            {
                MessageTemplate = "test event",
                Fields = fields
            });
    }
}
