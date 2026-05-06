using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Logging;

/// <summary>
/// Enforces a consistent structured log contract for fail-closed deny paths.
/// </summary>
public static class FailClosedDiagnostics
{
    public const string PolicyName = "fail-closed";
    public const int SchemaVersion = 1;

    public static void LogDenied(
        string capabilityId,
        string stage,
        string reasonCode,
        string message,
        LogSubsystem subsystem,
        in EngineLogContext context = default,
        IReadOnlyDictionary<string, object> fields = null,
        LogSeverity severity = LogSeverity.Warn,
        LogMarker marker = LogMarker.Invariant)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            throw new ArgumentException("Capability ID is required.", nameof(capabilityId));
        }

        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException("Stage is required.", nameof(stage));
        }

        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ArgumentException("Reason code is required.", nameof(reasonCode));
        }

        var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["policy"] = PolicyName,
            ["decision"] = "deny",
            ["capabilityId"] = capabilityId.Trim(),
            ["stage"] = stage.Trim(),
            ["reasonCode"] = reasonCode.Trim(),
            ["schemaVersion"] = SchemaVersion
        };

        if (fields != null)
        {
            foreach (var pair in fields)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                {
                    continue;
                }

                payload[pair.Key] = pair.Value;
            }
        }

        EngineLog.Write(
            subsystem,
            severity,
            message ?? string.Empty,
            marker,
            context,
            payload);
    }
}

public static class FailClosedReasonCodes
{
    public const string CspConnectSrcBlocked = "CSP_CONNECT_SRC_BLOCKED";
    public const string CorsOriginContextMissing = "CORS_ORIGIN_CONTEXT_MISSING";
    public const string CorsResponseDisallowed = "CORS_RESPONSE_DISALLOWED";
    public const string CorsPreflightBlocked = "CORS_PREFLIGHT_BLOCKED";
}
