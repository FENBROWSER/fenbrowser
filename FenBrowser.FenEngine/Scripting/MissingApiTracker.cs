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
    public bool AssignmentBeforeRead { get; init; }
    public bool KnownWebIdlMember { get; init; }
    public string DefinedInterface { get; init; } = string.Empty;
    public bool? ReceiverMatchesDefinedInterface { get; init; }
}

internal static class MissingApiTracker
{
    private const string Schema = "fenbrowser.missing-apis.v2";
    private const string TraceCategory = "MissingAPI";
    private const string EventName = "MissingApiObserved";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, SiteMissingApis> Sites = new(StringComparer.Ordinal);
    private static string _outputRootOverrideForTests;

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
            string outputPath;

            lock (Sync)
            {
                var siteKey = BuildSiteKey(observation.SiteUrl);
                var site = GetOrCreateSiteLocked(siteKey, observation.SiteUrl);
                if (!site.Records.TryGetValue(observation.ApiName, out var record))
                {
                    record = CreateRecord(observation);
                    site.Records[observation.ApiName] = record;
                }
                else
                {
                    record.EncounterCount++;
                    record.LastSeenUtc = Now();
                    if (string.IsNullOrWhiteSpace(record.ExceptionText) &&
                        !string.IsNullOrWhiteSpace(observation.ExceptionText))
                    {
                        record.ExceptionText = observation.ExceptionText;
                    }
                }

                outputPath = WriteSiteSnapshotLocked(siteKey, site);
                recordSnapshot = record.Clone();
            }

            WriteTrace(recordSnapshot, outputPath);
        }
        catch
        {
            // Diagnostics must never perturb page execution.
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
        lock (Sync)
        {
            Sites.Clear();
            _outputRootOverrideForTests = null;
        }
    }

    private static MissingApiRecord CreateRecord(MissingApiObservation observation)
    {
        var now = Now();
        var classification = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            observation.ObjectOrPrototype,
            observation.PropertyName,
            observation.OperationKind,
            observation.AssignmentBeforeRead,
            observation.KnownWebIdlMember,
            observation.DefinedInterface,
            observation.ReceiverMatchesDefinedInterface));
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
            ClassificationReason = classification.Reason,
            StandardPriorityEligible = classification.StandardPriorityEligible,
            ReceiverType = observation.ReceiverType ?? string.Empty,
            AssignmentBeforeRead = observation.AssignmentBeforeRead,
            KnownWebIdlMember = observation.KnownWebIdlMember,
            DefinedInterface = observation.DefinedInterface ?? string.Empty
        };
    }

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

    private static string WriteSiteSnapshotLocked(string siteKey, SiteMissingApis site)
    {
        var outputPath = Path.Combine(ResolveOutputRoot(), siteKey, "missing_apis.json");
        var output = new MissingApiSiteOutput
        {
            Schema = Schema,
            GeneratedAtUtc = Now(),
            SiteKey = site.SiteKey,
            SiteUrl = site.SiteUrl,
            Records = site.Records.Values
                .OrderBy(record => record.ApiName, StringComparer.Ordinal)
                .Select(record => record.Clone())
                .ToList()
        };

        var json = JsonSerializer.Serialize(output, JsonOptions);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        return outputPath;
    }

    private static void WriteTrace(MissingApiRecord record, string outputPath)
    {
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
            ["classificationReason"] = record.ClassificationReason,
            ["standardPriorityEligible"] = record.StandardPriorityEligible,
            ["receiverType"] = record.ReceiverType,
            ["assignmentBeforeRead"] = record.AssignmentBeforeRead,
            ["knownWebIdlMember"] = record.KnownWebIdlMember,
            ["definedInterface"] = record.DefinedInterface,
            ["outputPath"] = outputPath ?? string.Empty
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
            LogSeverity.Warn,
            "[FenJsBridge] Missing browser API observed",
            LogMarker.Unimplemented,
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
        public string Classification { get; init; } = "UNCLASSIFIED";
        public string OperationKind { get; init; } = "READ";
        public string ClassificationReason { get; init; } = string.Empty;
        public bool StandardPriorityEligible { get; init; }
        public string ReceiverType { get; init; } = string.Empty;
        public bool AssignmentBeforeRead { get; init; }
        public bool KnownWebIdlMember { get; init; }
        public string DefinedInterface { get; init; } = string.Empty;

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
                ClassificationReason = ClassificationReason,
                StandardPriorityEligible = StandardPriorityEligible,
                ReceiverType = ReceiverType,
                AssignmentBeforeRead = AssignmentBeforeRead,
                KnownWebIdlMember = KnownWebIdlMember,
                DefinedInterface = DefinedInterface
            };
    }
}
