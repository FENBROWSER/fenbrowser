using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tooling;

public sealed record DebugSiteExceptionRecord(string Source, string Type, string Message);

public sealed record DebugSiteExceptionSummary(
    int SchemaVersion,
    int TotalCount,
    int CallbackFailureCount,
    int RetainedCallbackFailureCount,
    int OtherExceptionCount,
    IReadOnlyList<BrowserCallbackFailureRecord> CallbackFailures,
    IReadOnlyList<DebugSiteExceptionRecord> OtherExceptions);

public static class DebugSiteExceptionSummaryBuilder
{
    public static DebugSiteExceptionSummary Build(
        BrowserEventLoopSnapshot eventLoop,
        IReadOnlyList<DebugSiteExceptionRecord> otherExceptions)
    {
        var callbackFailures = eventLoop?.CallbackFailureRecords?
            .OrderBy(static failure => failure.Sequence)
            .ToList() ?? new List<BrowserCallbackFailureRecord>();
        otherExceptions ??= Array.Empty<DebugSiteExceptionRecord>();
        var callbackFailureCount = eventLoop?.CallbackFailures ?? 0;

        return new DebugSiteExceptionSummary(
            SchemaVersion: 1,
            TotalCount: callbackFailureCount + otherExceptions.Count,
            CallbackFailureCount: callbackFailureCount,
            RetainedCallbackFailureCount: callbackFailures.Count,
            OtherExceptionCount: otherExceptions.Count,
            CallbackFailures: callbackFailures,
            OtherExceptions: otherExceptions);
    }
}
