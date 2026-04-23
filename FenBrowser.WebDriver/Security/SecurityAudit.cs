using System;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.WebDriver.Security
{
    public static class SecurityBlockReasons
    {
        public const string OriginNotAllowed = "origin-not-allowed";
        public const string PreflightRejected = "preflight-rejected";
        public const string NavigationUrlInvalid = "navigation-url-invalid";
        public const string NavigationUrlBlocked = "navigation-url-blocked";
        public const string ScriptBlocked = "script-blocked";
        public const string CapabilityPolicyViolation = "capability-policy-violation";
        public const string SessionIsolationViolation = "session-isolation-violation";
    }

    public sealed class SecurityFailureData
    {
        public string Reason { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
    }

    public static class SecurityAudit
    {
        public static void LogBlocked(string reasonCode, string detail, string sessionId = "")
        {
            var sessionSuffix = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : $" session={sessionId}";
            EngineLogCompat.Warn(
                $"[WebDriver.Security] BLOCKED reason={reasonCode}{sessionSuffix} detail={detail}",
                LogCategory.Security);
        }

        public static SecurityFailureData CreateFailureData(string reasonCode, string detail, string sessionId = "")
        {
            return new SecurityFailureData
            {
                Reason = reasonCode ?? string.Empty,
                Detail = detail ?? string.Empty,
                SessionId = sessionId ?? string.Empty
            };
        }

        public static string BuildBlockedMessage(string reasonCode)
        {
            return $"Blocked by WebDriver security policy (reason={reasonCode})";
        }
    }
}
