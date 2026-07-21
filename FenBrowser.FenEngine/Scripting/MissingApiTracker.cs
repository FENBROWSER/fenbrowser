using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

internal sealed class MissingApiObservation
{
    public string ApiName { get; init; } = string.Empty;
    public string ObjectOrPrototype { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public string SiteUrl { get; init; } = string.Empty;
    public string ScriptUrl { get; init; } = string.Empty;
    public string ScriptId { get; init; } = string.Empty;
    public string NavigationId { get; init; } = string.Empty;
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ExceptionText { get; init; } = string.Empty;
    public MissingApiOperationKind OperationKind { get; init; } = MissingApiOperationKind.Read;
    public string ReceiverType { get; init; } = string.Empty;
    public bool AssignmentObserved { get; init; }
    public bool AssignmentBeforeRead { get; init; }
    public bool FunctionPrototypeMarkerObserved { get; init; }
    public bool DescriptorTargetIsPrototype { get; init; }
    public bool KnownWebIdlMember { get; init; }
    public string DefinedInterface { get; init; } = string.Empty;
    public bool? ReceiverMatchesDefinedInterface { get; init; }
}

internal sealed class BrowserMissingApiSnapshot
{
    public int SchemaVersion { get; init; } = 2;
    public string Schema { get; init; } = "fenbrowser.missing-apis.v2";
    public string GeneratedAtUtc { get; init; } = string.Empty;
    public string SiteKey { get; init; } = string.Empty;
    public string SiteUrl { get; init; } = string.Empty;
    public int TotalRecordCount { get; init; }
    public int RetainedRecordCount { get; init; }
    public bool Truncated { get; init; }
    public List<BrowserMissingApiRecordSnapshot> Records { get; init; } = new();
}

internal sealed class BrowserMissingApiRecordSnapshot
{
    public string ApiName { get; init; } = string.Empty;
    public string ObjectOrPrototype { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public string SiteUrl { get; init; } = string.Empty;
    public string ScriptUrl { get; init; } = string.Empty;
    public string ScriptId { get; init; } = string.Empty;
    public string NavigationId { get; init; } = string.Empty;
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string FirstSeenTraceId { get; init; } = string.Empty;
    public string FirstSeenUtc { get; init; } = string.Empty;
    public string LastSeenUtc { get; init; } = string.Empty;
    public int EncounterCount { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ExceptionText { get; init; } = string.Empty;
    public string Classification { get; init; } = "UNCLASSIFIED";
    public string OperationKind { get; init; } = "READ";
    public List<string> OperationKindsObserved { get; init; } = new();
    public string ClassificationReason { get; init; } = string.Empty;
    public bool StandardPriorityEligible { get; init; }
    public string ReceiverType { get; init; } = string.Empty;
    public bool AssignmentObserved { get; init; }
    public bool AssignmentBeforeRead { get; init; }
    public bool FunctionPrototypeMarkerObserved { get; init; }
    public bool DescriptorTargetIsPrototype { get; init; }
    public bool KnownWebIdlMember { get; init; }
    public string DefinedInterface { get; init; } = string.Empty;
    public bool? ReceiverMatchesDefinedInterface { get; init; }
}

internal static class MissingApiTracker
{
    private const string Schema = "fenbrowser.missing-apis.v2";
    private const string TraceCategory = "MissingAPI";
    private const string EventName = "MissingApiObserved";
    internal const int SnapshotRecordLimit = 512;
    internal const int MaxExceptionTextLength = 4096;

    /// <summary>Debounce interval for snapshot writes. Tests may override via SetDebounceForTests.</summary>
    private static TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, SiteMissingApis> Sites = new(StringComparer.Ordinal);
    private static string _outputRootOverrideForTests;
    private static TimeSpan? _debounceOverrideForTests;

    // Debounced snapshot writer state.
    private static readonly object FlushSync = new();
    private static Task _pendingFlushTask;
    private static CancellationTokenSource _flushCts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Record(MissingApiObservation observation)
    {
        if (observation == null || string.IsNullOrWhiteSpace(observation.ApiName))
        {
            return;
        }

        try
        {
            MissingApiRecord recordSnapshot;
            string previousClassification;
            bool firstEncounter;
            bool classificationChanged;
            string siteKey;

            lock (Sync)
            {
                siteKey = BuildSiteKey(observation.SiteUrl);
                var site = GetOrCreateSiteLocked(siteKey, observation.SiteUrl);
                var recordKey = BuildRecordKey(observation);

                if (!site.Records.TryGetValue(recordKey, out var record))
                {
                    // First encounter — create new record.
                    record = CreateRecord(observation);
                    site.Records[recordKey] = record;
                    firstEncounter = true;
                    classificationChanged = false;
                    previousClassification = record.Classification;
                }
                else
                {
                    firstEncounter = false;
                    previousClassification = record.Classification;

                    record.EncounterCount++;
                    record.LastSeenUtc = Now();

                    var evidenceChanged = record.AddOperation(observation.OperationKind);
                    var assignmentObserved = observation.AssignmentObserved ||
                        observation.OperationKind == MissingApiOperationKind.Write;
                    if (assignmentObserved && !record.AssignmentObserved)
                    {
                        record.AssignmentObserved = true;
                        evidenceChanged = true;
                    }
                    if (observation.AssignmentBeforeRead && !record.AssignmentBeforeRead)
                    {
                        record.AssignmentBeforeRead = true;
                        evidenceChanged = true;
                    }
                    if (observation.FunctionPrototypeMarkerObserved && !record.FunctionPrototypeMarkerObserved)
                    {
                        record.FunctionPrototypeMarkerObserved = true;
                        evidenceChanged = true;
                    }
                    if (evidenceChanged)
                    {
                        record.RefreshClassification();
                    }

                    classificationChanged = !string.Equals(
                        previousClassification, record.Classification, StringComparison.Ordinal);

                    // Capture first exception text; truncate to reasonable maximum.
                    if (string.IsNullOrWhiteSpace(record.ExceptionText) &&
                        !string.IsNullOrWhiteSpace(observation.ExceptionText))
                    {
                        record.ExceptionText = TruncateExceptionText(observation.ExceptionText);
                    }
                }

                // Mark the site dirty for deferred snapshot writing.
                site.Dirty = true;

                // Clone for trace emission outside the lock.
                recordSnapshot = record.Clone();
            }

            // Determine whether to emit a structured diagnostic event.
            var classification = MissingApiClassifier.ParseClassificationToken(recordSnapshot.Classification);
            var shouldEmit = firstEncounter || classificationChanged;

            if (shouldEmit)
            {
                var diag = GetDiagnosticSeverity(classification, firstEncounter: true);
                if (diag.HasValue)
                {
                    WriteTrace(recordSnapshot);
                }
            }

            // Schedule debounced snapshot write (outside the main lock).
            ScheduleSnapshotFlush(siteKey);
        }
        catch (Exception ex)
        {
            EngineLog.Write(
                LogSubsystem.Js,
                LogSeverity.Warn,
                $"[MissingAPI] Failed to record diagnostic observation: {ex.GetType().Name}: {ex.Message}",
                LogMarker.Unexpected,
                fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event"] = "MissingApiRecordFailed",
                    ["exceptionType"] = ex.GetType().FullName ?? ex.GetType().Name
                });
        }
    }

    internal static BrowserMissingApiSnapshot GetSnapshot(string siteUrl)
    {
        lock (Sync)
        {
            IEnumerable<SiteMissingApis> selectedSites = Sites.Values;
            if (!string.IsNullOrWhiteSpace(siteUrl))
            {
                var siteKey = BuildSiteKey(siteUrl);
                selectedSites = Sites.TryGetValue(siteKey, out var site)
                    ? new[] { site }
                    : Array.Empty<SiteMissingApis>();
            }

            var ordered = selectedSites
                .SelectMany(site => site.Records.Values)
                .OrderBy(record => record.FirstSeenUtc, StringComparer.Ordinal)
                .ThenBy(record => record.ApiName, StringComparer.Ordinal)
                .ThenBy(record => record.NavigationId, StringComparer.Ordinal)
                .ThenBy(record => record.ScriptId, StringComparer.Ordinal)
                .ThenBy(record => record.ReceiverType, StringComparer.Ordinal)
                .ToList();
            var retained = ordered.Take(SnapshotRecordLimit).Select(ToSnapshot).ToList();
            var selectedSiteList = selectedSites.ToList();
            return new BrowserMissingApiSnapshot
            {
                GeneratedAtUtc = Now(),
                SiteKey = selectedSiteList.Count switch
                {
                    0 => "none",
                    1 => selectedSiteList[0].SiteKey,
                    _ => "multiple"
                },
                SiteUrl = selectedSiteList.Count == 1 ? selectedSiteList[0].SiteUrl : string.Empty,
                TotalRecordCount = ordered.Count,
                RetainedRecordCount = retained.Count,
                Truncated = ordered.Count > retained.Count,
                Records = retained
            };
        }
    }

    internal static void ResetForRun()
    {
        lock (Sync)
        {
            Sites.Clear();
        }
    }

    internal static string GetOutputPathForTests(Uri siteUri)
    {
        var siteKey = BuildSiteKey(siteUri?.AbsoluteUri);
        return Path.Combine(ResolveOutputRoot(), siteKey, "missing_apis.json");
    }

    internal static void ConfigureForTests(string outputRoot)
    {
        lock (Sync)
        {
            Sites.Clear();
            _outputRootOverrideForTests = outputRoot;
        }
    }

    internal static void ResetForTests()
    {
        CancelPendingFlush();
        lock (Sync)
        {
            Sites.Clear();
            _outputRootOverrideForTests = null;
            _debounceOverrideForTests = null;
        }
    }

    /// <summary>
    /// Override the debounce interval for tests (pass null to restore default).
    /// </summary>
    internal static void SetDebounceForTests(TimeSpan? interval)
    {
        lock (Sync)
        {
            _debounceOverrideForTests = interval;
        }
    }

    /// <summary>
    /// Flush all pending snapshot writes synchronously. Call during
    /// navigation-end diagnostics capture, failure-bundle export, shutdown, or test cleanup.
    /// </summary>
    internal static Task FlushPendingWritesAsync(CancellationToken cancellationToken = default)
    {
        Task pending;
        lock (FlushSync)
        {
            pending = _pendingFlushTask;
        }

        if (pending == null || pending.IsCompleted)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAny(pending, Task.Delay(Timeout.Infinite, cancellationToken));
    }

    /// <summary>
    /// Maps a classification to the diagnostic severity that should be emitted
    /// for the first encounter (or classification change) of a missing API.
    /// Returns null when no structured event should be emitted at all.
    /// </summary>
    private static (LogSeverity severity, LogMarker marker)? GetDiagnosticSeverity(
        MissingApiClassification classification,
        bool firstEncounter)
    {
        switch (classification)
        {
            case MissingApiClassification.StandardApi:
                if (firstEncounter)
                {
                    return (LogSeverity.Warn, LogMarker.Unimplemented);
                }

                // Repeated encounters: no individual event (tracked in snapshot only).
                return null;

            case MissingApiClassification.Unclassified:
                if (firstEncounter)
                {
                    return (LogSeverity.Debug, LogMarker.None);
                }

                return null;

            case MissingApiClassification.WrongReceiver:
                if (firstEncounter)
                {
                    return (LogSeverity.Debug, LogMarker.None);
                }

                return null;

            case MissingApiClassification.SiteExpando:
                // Do not emit a warning. Track in snapshot only.
                // Optional Trace event in developer mode.
                return null;

            case MissingApiClassification.LegacyProbe:
                // Do not emit a warning. Track in snapshot only.
                // Optional Trace event in developer mode.
                return null;

            default:
                return null;
        }
    }

    private static void CancelPendingFlush()
    {
        CancellationTokenSource cts;
        lock (FlushSync)
        {
            cts = _flushCts;
            _flushCts = null;
            _pendingFlushTask = null;
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch
        {
            // best-effort
        }
    }

    private static void ScheduleSnapshotFlush(string siteKey)
    {
        TimeSpan debounce;
        lock (Sync)
        {
            debounce = _debounceOverrideForTests ?? DebounceInterval;
        }

        // For zero-interval (test mode), flush immediately.
        if (debounce <= TimeSpan.Zero)
        {
            FlushSiteNow(siteKey);
            return;
        }

        lock (FlushSync)
        {
            CancelPendingFlushUnsafe();

            var cts = new CancellationTokenSource();
            _flushCts = cts;
            _pendingFlushTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(debounce, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                FlushAllDirtySites();
            }, cts.Token);
        }
    }

    private static void CancelPendingFlushUnsafe()
    {
        // Must be called under FlushSync lock.
        var cts = _flushCts;
        _flushCts = null;
        _pendingFlushTask = null;

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch
        {
            // best-effort
        }
    }

    private static void FlushSiteNow(string siteKey)
    {
        SiteMissingApis site;
        List<MissingApiRecord> snapshot;

        lock (Sync)
        {
            if (!Sites.TryGetValue(siteKey, out site))
            {
                return;
            }

            if (!site.Dirty)
            {
                return;
            }

            snapshot = site.Records.Values.Select(r => r.Clone()).ToList();
            site.Dirty = false;
            site.Generation++;
        }

        WriteSnapshotAtomic(siteKey, site.SiteUrl, snapshot);
    }

    private static void FlushAllDirtySites()
    {
        List<(string SiteKey, string SiteUrl, List<MissingApiRecord> Records)> dirtySites = new();

        lock (Sync)
        {
            foreach (var (key, site) in Sites)
            {
                if (!site.Dirty)
                {
                    continue;
                }

                var snapshot = site.Records.Values.Select(r => r.Clone()).ToList();
                site.Dirty = false;
                site.Generation++;
                dirtySites.Add((key, site.SiteUrl, snapshot));
            }
        }

        foreach (var (siteKey, siteUrl, records) in dirtySites)
        {
            WriteSnapshotAtomic(siteKey, siteUrl, records);
        }
    }

    private static void WriteSnapshotAtomic(string siteKey, string siteUrl, List<MissingApiRecord> records)
    {
        var outputPath = Path.Combine(ResolveOutputRoot(), siteKey, "missing_apis.json");
        var dir = Path.GetDirectoryName(outputPath);

        try
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var output = new MissingApiSiteOutput
            {
                Schema = Schema,
                GeneratedAtUtc = Now(),
                SiteKey = siteKey,
                SiteUrl = siteUrl ?? string.Empty,
                Records = records
                    .OrderBy(r => r.ApiName, StringComparer.Ordinal)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(output, JsonOptions);

            // Write to temp file then atomically replace.
            var tmpPath = outputPath + ".tmp";
            File.WriteAllText(tmpPath, json, new UTF8Encoding(false));

            try
            {
                File.Move(tmpPath, outputPath, overwrite: true);
            }
            catch
            {
                // If Move fails (e.g. antivirus), try direct overwrite as fallback.
                File.WriteAllText(outputPath, json, new UTF8Encoding(false));
                try { File.Delete(tmpPath); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            EngineLog.Write(
                LogSubsystem.Js,
                LogSeverity.Warn,
                $"[MissingAPI] Failed to write snapshot for site '{siteKey}': {ex.GetType().Name}: {ex.Message}",
                LogMarker.Unexpected,
                fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["event"] = "MissingApiSnapshotWriteFailed",
                    ["exceptionType"] = ex.GetType().FullName ?? ex.GetType().Name,
                    ["siteKey"] = siteKey
                });
        }
    }

    private static MissingApiRecord CreateRecord(MissingApiObservation observation)
    {
        var now = Now();
        var assignmentObserved = observation.AssignmentObserved ||
            observation.OperationKind == MissingApiOperationKind.Write;
        var classification = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            observation.ObjectOrPrototype,
            observation.PropertyName,
            observation.OperationKind,
            observation.AssignmentBeforeRead,
            observation.KnownWebIdlMember,
            observation.DefinedInterface,
            observation.ReceiverMatchesDefinedInterface,
            assignmentObserved,
            observation.FunctionPrototypeMarkerObserved,
            observation.DescriptorTargetIsPrototype));
        return new MissingApiRecord
        {
            ApiName = observation.ApiName.Trim(),
            ObjectOrPrototype = observation.ObjectOrPrototype ?? string.Empty,
            PropertyName = observation.PropertyName ?? string.Empty,
            SiteUrl = observation.SiteUrl ?? string.Empty,
            ScriptUrl = observation.ScriptUrl ?? string.Empty,
            ScriptId = observation.ScriptId ?? string.Empty,
            NavigationId = observation.NavigationId ?? string.Empty,
            Line = observation.Line,
            Column = observation.Column,
            FirstSeenTraceId = "missing-api-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            FirstSeenUtc = now,
            LastSeenUtc = now,
            EncounterCount = 1,
            Reason = observation.Reason ?? string.Empty,
            ExceptionText = observation.ExceptionText ?? string.Empty,
            Classification = MissingApiClassifier.ToToken(classification.Classification),
            OperationKind = MissingApiClassifier.ToToken(classification.OperationKind),
            OperationKindsObserved = new List<string>
            {
                MissingApiClassifier.ToToken(observation.OperationKind)
            },
            ClassificationReason = classification.Reason,
            StandardPriorityEligible = classification.StandardPriorityEligible,
            ReceiverType = observation.ReceiverType ?? string.Empty,
            AssignmentObserved = assignmentObserved,
            AssignmentBeforeRead = observation.AssignmentBeforeRead,
            FunctionPrototypeMarkerObserved = observation.FunctionPrototypeMarkerObserved,
            DescriptorTargetIsPrototype = observation.DescriptorTargetIsPrototype,
            KnownWebIdlMember = classification.KnownWebIdlMember,
            DefinedInterface = classification.DefinedInterface,
            ReceiverMatchesDefinedInterface = classification.ReceiverMatchesDefinedInterface
        };
    }

    private static string BuildRecordKey(MissingApiObservation observation)
        => string.Join(
            "\u001f",
            observation.NavigationId ?? string.Empty,
            observation.ScriptId ?? string.Empty,
            string.IsNullOrWhiteSpace(observation.ReceiverType)
                ? observation.ObjectOrPrototype ?? string.Empty
                : observation.ReceiverType,
            observation.ApiName ?? string.Empty);

    private static BrowserMissingApiRecordSnapshot ToSnapshot(MissingApiRecord record)
        => new()
        {
            ApiName = record.ApiName,
            ObjectOrPrototype = record.ObjectOrPrototype,
            PropertyName = record.PropertyName,
            SiteUrl = record.SiteUrl,
            ScriptUrl = record.ScriptUrl,
            ScriptId = record.ScriptId,
            NavigationId = record.NavigationId,
            Line = record.Line,
            Column = record.Column,
            FirstSeenTraceId = record.FirstSeenTraceId,
            FirstSeenUtc = record.FirstSeenUtc,
            LastSeenUtc = record.LastSeenUtc,
            EncounterCount = record.EncounterCount,
            Reason = record.Reason,
            ExceptionText = record.ExceptionText,
            Classification = record.Classification,
            OperationKind = record.OperationKind,
            OperationKindsObserved = new List<string>(record.OperationKindsObserved),
            ClassificationReason = record.ClassificationReason,
            StandardPriorityEligible = record.StandardPriorityEligible,
            ReceiverType = record.ReceiverType,
            AssignmentObserved = record.AssignmentObserved,
            AssignmentBeforeRead = record.AssignmentBeforeRead,
            FunctionPrototypeMarkerObserved = record.FunctionPrototypeMarkerObserved,
            DescriptorTargetIsPrototype = record.DescriptorTargetIsPrototype,
            KnownWebIdlMember = record.KnownWebIdlMember,
            DefinedInterface = record.DefinedInterface,
            ReceiverMatchesDefinedInterface = record.ReceiverMatchesDefinedInterface
        };

    private static SiteMissingApis GetOrCreateSiteLocked(string siteKey, string siteUrl)
    {
        if (!Sites.TryGetValue(siteKey, out var site))
        {
            site = new SiteMissingApis
            {
                SiteKey = siteKey,
                SiteUrl = siteUrl ?? string.Empty
            };
            Sites[siteKey] = site;
        }
        else if (string.IsNullOrWhiteSpace(site.SiteUrl) && !string.IsNullOrWhiteSpace(siteUrl))
        {
            site.SiteUrl = siteUrl;
        }

        return site;
    }

    private static void WriteTrace(MissingApiRecord record)
    {
        var classification = MissingApiClassifier.ParseClassificationToken(record.Classification);
        var diag = GetDiagnosticSeverity(classification, firstEncounter: true);
        var severity = diag?.severity ?? LogSeverity.Debug;
        var marker = diag?.marker ?? LogMarker.None;

        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["event"] = EventName,
            ["eventName"] = EventName,
            ["traceCategory"] = TraceCategory,
            ["apiName"] = record.ApiName,
            ["objectOrPrototype"] = record.ObjectOrPrototype,
            ["propertyName"] = record.PropertyName,
            ["siteUrl"] = record.SiteUrl,
            ["scriptUrl"] = record.ScriptUrl,
            ["scriptId"] = record.ScriptId,
            ["navigationId"] = record.NavigationId,
            ["firstSeenTraceId"] = record.FirstSeenTraceId,
            ["encounterCount"] = record.EncounterCount,
            ["reason"] = record.Reason,
            ["exceptionText"] = record.ExceptionText,
            ["classification"] = record.Classification,
            ["operationKind"] = record.OperationKind,
            ["operationKindsObserved"] = record.OperationKindsObserved.ToArray(),
            ["classificationReason"] = record.ClassificationReason,
            ["standardPriorityEligible"] = record.StandardPriorityEligible,
            ["receiverType"] = record.ReceiverType,
            ["assignmentObserved"] = record.AssignmentObserved,
            ["assignmentBeforeRead"] = record.AssignmentBeforeRead,
            ["knownWebIdlMember"] = record.KnownWebIdlMember,
            ["definedInterface"] = record.DefinedInterface,
            ["receiverMatchesDefinedInterface"] = record.ReceiverMatchesDefinedInterface
        };

        if (record.Line.HasValue)
        {
            fields["line"] = record.Line.Value;
            fields["scriptSourceLine"] = record.Line.Value;
        }

        if (record.Column.HasValue)
        {
            fields["column"] = record.Column.Value;
            fields["scriptSourceColumn"] = record.Column.Value;
        }

        EngineLog.Write(
            LogSubsystem.Js,
            severity,
            $"[FenJsBridge] Missing browser API: {record.ApiName} [{record.Classification}]",
            marker,
            new EngineLogContext(
                NavigationId: string.IsNullOrWhiteSpace(record.NavigationId)
                    ? LogContext.CurrentCorrelationId
                    : record.NavigationId,
                Url: record.SiteUrl,
                ResourceUrl: record.ScriptUrl,
                ScriptId: record.ScriptId,
                SpecArea: "WebIDL"),
            fields);
    }

    private static string TruncateExceptionText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Length <= MaxExceptionTextLength)
        {
            return text;
        }

        return text.Substring(0, MaxExceptionTextLength);
    }

    private static string ResolveOutputRoot()
    {
        if (!string.IsNullOrWhiteSpace(_outputRootOverrideForTests))
        {
            return _outputRootOverrideForTests;
        }

        return Path.Combine(DiagnosticPaths.GetLogsDirectory(), "missing_apis");
    }

    private static string BuildSiteKey(string siteUrl)
    {
        if (Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.IdnHost;
            if (!uri.IsDefaultPort)
            {
                host += "_" + uri.Port.ToString(CultureInfo.InvariantCulture);
            }

            return SanitizePathSegment(host);
        }

        return "unknown-site";
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown-site";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }

        var sanitized = builder.ToString().Trim('.', '_');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "unknown-site";
        }

        return sanitized.Length <= 96 ? sanitized : sanitized.Substring(0, 96);
    }

    private static string Now()
        => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed class SiteMissingApis
    {
        public string SiteKey { get; init; } = string.Empty;
        public string SiteUrl { get; set; } = string.Empty;
        public Dictionary<string, MissingApiRecord> Records { get; } = new(StringComparer.Ordinal);
        public bool Dirty { get; set; }
        public long Generation { get; set; }
    }

    private sealed class MissingApiSiteOutput
    {
        public string Schema { get; init; } = string.Empty;
        public string GeneratedAtUtc { get; init; } = string.Empty;
        public string SiteKey { get; init; } = string.Empty;
        public string SiteUrl { get; init; } = string.Empty;
        public List<MissingApiRecord> Records { get; init; } = new();
    }

    private sealed class MissingApiRecord
    {
        public string ApiName { get; init; } = string.Empty;
        public string ObjectOrPrototype { get; init; } = string.Empty;
        public string PropertyName { get; init; } = string.Empty;
        public string SiteUrl { get; init; } = string.Empty;
        public string ScriptUrl { get; init; } = string.Empty;
        public string ScriptId { get; init; } = string.Empty;
        public string NavigationId { get; init; } = string.Empty;
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string FirstSeenTraceId { get; init; } = string.Empty;
        public string FirstSeenUtc { get; init; } = string.Empty;
        public string LastSeenUtc { get; set; } = string.Empty;
        public int EncounterCount { get; set; }
        public string Reason { get; init; } = string.Empty;
        public string ExceptionText { get; set; } = string.Empty;
        public string Classification { get; set; } = "UNCLASSIFIED";
        public string OperationKind { get; init; } = "READ";
        public List<string> OperationKindsObserved { get; init; } = new();
        public string ClassificationReason { get; set; } = string.Empty;
        public bool StandardPriorityEligible { get; set; }
        public string ReceiverType { get; init; } = string.Empty;
        public bool AssignmentObserved { get; set; }
        public bool AssignmentBeforeRead { get; set; }
        public bool FunctionPrototypeMarkerObserved { get; set; }
        public bool DescriptorTargetIsPrototype { get; set; }
        public bool KnownWebIdlMember { get; set; }
        public string DefinedInterface { get; set; } = string.Empty;
        public bool? ReceiverMatchesDefinedInterface { get; set; }

        public bool AddOperation(MissingApiOperationKind operationKind)
        {
            var token = MissingApiClassifier.ToToken(operationKind);
            if (OperationKindsObserved.Contains(token, StringComparer.Ordinal))
            {
                return false;
            }

            OperationKindsObserved.Add(token);
            return true;
        }

        public void RefreshClassification()
        {
            var classification = MissingApiClassifier.Classify(new MissingApiClassificationInput(
                ObjectOrPrototype,
                PropertyName,
                ParseOperationKind(OperationKind),
                AssignmentBeforeRead,
                KnownWebIdlMember,
                DefinedInterface,
                ReceiverMatchesDefinedInterface,
                AssignmentObserved,
                FunctionPrototypeMarkerObserved,
                DescriptorTargetIsPrototype));
            Classification = MissingApiClassifier.ToToken(classification.Classification);
            ClassificationReason = classification.Reason;
            StandardPriorityEligible = classification.StandardPriorityEligible;
            KnownWebIdlMember = classification.KnownWebIdlMember;
            DefinedInterface = classification.DefinedInterface;
            ReceiverMatchesDefinedInterface = classification.ReceiverMatchesDefinedInterface;
        }

        private static MissingApiOperationKind ParseOperationKind(string token)
            => token switch
            {
                "WRITE" => MissingApiOperationKind.Write,
                "DELETE" => MissingApiOperationKind.Delete,
                "IN_CHECK" => MissingApiOperationKind.InCheck,
                "PROTOTYPE_ACCESS" => MissingApiOperationKind.PrototypeAccess,
                "CALL" => MissingApiOperationKind.Call,
                "CONSTRUCT" => MissingApiOperationKind.Construct,
                "DESCRIPTOR_OPERATION" => MissingApiOperationKind.DescriptorOperation,
                _ => MissingApiOperationKind.Read
            };

        public MissingApiRecord Clone()
            => new()
            {
                ApiName = ApiName,
                ObjectOrPrototype = ObjectOrPrototype,
                PropertyName = PropertyName,
                SiteUrl = SiteUrl,
                ScriptUrl = ScriptUrl,
                ScriptId = ScriptId,
                NavigationId = NavigationId,
                Line = Line,
                Column = Column,
                FirstSeenTraceId = FirstSeenTraceId,
                FirstSeenUtc = FirstSeenUtc,
                LastSeenUtc = LastSeenUtc,
                EncounterCount = EncounterCount,
                Reason = Reason,
                ExceptionText = ExceptionText,
                Classification = Classification,
                OperationKind = OperationKind,
                OperationKindsObserved = new List<string>(OperationKindsObserved),
                ClassificationReason = ClassificationReason,
                StandardPriorityEligible = StandardPriorityEligible,
                ReceiverType = ReceiverType,
                AssignmentObserved = AssignmentObserved,
                AssignmentBeforeRead = AssignmentBeforeRead,
                FunctionPrototypeMarkerObserved = FunctionPrototypeMarkerObserved,
                DescriptorTargetIsPrototype = DescriptorTargetIsPrototype,
                KnownWebIdlMember = KnownWebIdlMember,
                DefinedInterface = DefinedInterface,
                ReceiverMatchesDefinedInterface = ReceiverMatchesDefinedInterface
            };
    }
}
