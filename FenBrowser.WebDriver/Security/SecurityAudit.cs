using System;
using System.Text;
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
        private const int MaxReasonLength = 128;
        private const int MaxDetailLength = 2048;
        private const int MaxSessionIdLength = 256;

        public static void LogBlocked(string reasonCode, string detail, string sessionId = "")
        {
            var reason = SanitizeSingleLine(reasonCode, MaxReasonLength);
            var safeDetail = SanitizeSingleLine(detail, MaxDetailLength);
            var safeSessionId = SanitizeSingleLine(sessionId, MaxSessionIdLength);
            var sessionSuffix = safeSessionId.Length == 0 ? string.Empty : $" session={safeSessionId}";
            EngineLogCompat.Warn(
                $"[WebDriver.Security] BLOCKED reason={reason}{sessionSuffix} detail={safeDetail}",
                LogCategory.Security);
        }

        public static SecurityFailureData CreateFailureData(string reasonCode, string detail, string sessionId = "")
        {
            return new SecurityFailureData
            {
                Reason = SanitizeSingleLine(reasonCode, MaxReasonLength),
                Detail = SanitizeSingleLine(detail, MaxDetailLength),
                SessionId = SanitizeSingleLine(sessionId, MaxSessionIdLength)
            };
        }

        public static string BuildBlockedMessage(string reasonCode)
        {
            return $"Blocked by WebDriver security policy (reason={SanitizeSingleLine(reasonCode, MaxReasonLength)})";
        }

        private static string SanitizeSingleLine(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0)
                return string.Empty;

            var limit = Math.Min(value.Length, maxLength);
            StringBuilder builder = null;
            for (var i = 0; i < limit; i++)
            {
                var ch = value[i];
                var replacement = char.IsControl(ch) ? ' ' : ch;
                if (builder == null && replacement != ch)
                {
                    builder = new StringBuilder(limit);
                    builder.Append(value, 0, i);
                }

                builder?.Append(replacement);
            }

            var result = builder?.ToString() ?? value.Substring(0, limit);
            return value.Length > maxLength ? result + "…" : result;
        }
    }
}
