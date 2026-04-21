using System.Collections.Generic;

namespace FenBrowser.DevTools.Domains.DTOs;

public sealed record LogFilterPayload
{
    public string[]? Subsystems { get; init; }
    public string? TabId { get; init; }
    public string? FrameId { get; init; }
}

public sealed record LogEntryAddedEvent
{
    public required LogEntryPayload Entry { get; init; }
}

public sealed record LogCountersPayload
{
    public required IReadOnlyList<LogDocumentCounterEntry> Documents { get; init; }
}

public sealed record LogDocumentCounterEntry
{
    public string DocumentKey { get; init; } = string.Empty;
    public long TotalCount { get; init; }
    public long WarnCount { get; init; }
    public long ErrorCount { get; init; }
    public long FatalCount { get; init; }
    public long LastSequence { get; init; }
    public string LastTimestampUtc { get; init; } = string.Empty;
    public string? LastUrl { get; init; }
    public string? LastTabId { get; init; }
    public string? LastFrameId { get; init; }
}

public sealed record LogEntryPayload
{
    public string TimestampUtc { get; init; }
    public long Sequence { get; init; }
    public int ProcessId { get; init; }
    public int ThreadId { get; init; }
    public string Subsystem { get; init; }
    public string Severity { get; init; }
    public string Marker { get; init; }
    public string Message { get; init; }
    public string SourceFile { get; init; }
    public int SourceLine { get; init; }
    public string SourceMember { get; init; }
    public object Context { get; init; }
    public object? Fields { get; init; }
}
