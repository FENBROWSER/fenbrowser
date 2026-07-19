// SpecRef: RFC6265 (Set-Cookie / Cookie header semantics)
// CapabilityId: DIAG-COOKIES-01
// Determinism: strict
// FallbackPolicy: silent (diagnostics must never throw into the network path)
using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Storage
{
    /// <summary>
    /// Structured, PII-safe diagnostics for cookie ingress (Set-Cookie) and
    /// egress (outbound Cookie header).
    ///
    /// Cookie values and value-derived fingerprints are never logged.
    ///
    /// Gating: opt-in via env var <c>FEN_LOG_COOKIES=1</c> or the
    /// <see cref="BrowserSettings.LogSettings.LogCookies"/> setting.
    /// When disabled, callers pay a single static-boolean check.
    /// </summary>
    public static class CookieDiagnostics
    {
        private const string EnvVar = "FEN_LOG_COOKIES";

        private static readonly bool _envEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable(EnvVar),
                "1",
                StringComparison.Ordinal);

        public static bool Enabled
        {
            get
            {
                if (_envEnabled)
                {
                    return true;
                }

                try
                {
                    return BrowserSettings.Instance?.Logging?.LogCookies == true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Log an inbound Set-Cookie that has been accepted into the jar.</summary>
        public static void LogIngress(
            Uri responseUri,
            Cookie cookie,
            Uri topLevelDocumentUri,
            bool fromScript)
        {
            if (!Enabled || cookie == null)
            {
                return;
            }

            try
            {
                var entry = new LogEntry
                {
                    Category = LogCategory.Storage,
                    Level = LogLevel.Info,
                    Message = $"[COOKIE-IN] {cookie.Name}@{cookie.Domain} accepted=true src={(fromScript ? "script" : "header")}"
                };

                entry.WithData("event", "set-cookie");
                entry.WithData("source", fromScript ? "document.cookie" : "http-response");
                entry.WithData("name", cookie.Name);
                entry.WithData("accepted", true);
                entry.WithData("domain", cookie.Domain ?? string.Empty);
                entry.WithData("path", cookie.Path ?? "/");
                entry.WithData("secure", cookie.Secure);
                entry.WithData("httpOnly", cookie.HttpOnly);
                entry.WithData("sameSite", cookie.SameSite.ToString());
                entry.WithData("session", cookie.IsSession);
                if (cookie.Expires.HasValue)
                {
                    entry.WithData("expiresUtc", cookie.Expires.Value.UtcDateTime.ToString("o"));
                }
                if (cookie.IsPartitioned)
                {
                    entry.WithData("partitioned", true);
                }
                if (responseUri != null)
                {
                    entry.WithData("responseHost", responseUri.Host);
                    entry.WithData("responsePath", responseUri.AbsolutePath);
                }
                if (topLevelDocumentUri != null)
                {
                    entry.WithData("topLevelHost", topLevelDocumentUri.Host);
                }

                LogManager.Log(entry);
            }
            catch
            {
                // Diagnostics must never throw into the network/storage path.
            }
        }

        /// <summary>Log a Set-Cookie that was rejected before storage.</summary>
        public static void LogIngressRejected(
            Uri responseUri,
            string rawHeader,
            string reason,
            bool fromScript,
            Uri topLevelDocumentUri = null)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                var name = ExtractCookieName(rawHeader);
                var entry = new LogEntry
                {
                    Category = LogCategory.Storage,
                    Level = LogLevel.Warn,
                    Message = $"[COOKIE-REJECT] {name}@{responseUri?.Host} reason={reason} src={(fromScript ? "script" : "header")}"
                };

                entry.WithData("event", "set-cookie-rejected");
                entry.WithData("source", fromScript ? "document.cookie" : "http-response");
                entry.WithData("name", name);
                entry.WithData("accepted", false);
                entry.WithData("reason", reason ?? "unknown");
                if (responseUri != null)
                {
                    entry.WithData("responseHost", responseUri.Host);
                    entry.WithData("responsePath", responseUri.AbsolutePath);
                }
                if (topLevelDocumentUri != null)
                {
                    entry.WithData("topLevelHost", topLevelDocumentUri.Host);
                }

                LogManager.Log(entry);
            }
            catch
            {
                // swallow
            }
        }

        /// <summary>Log the outbound Cookie header attached to a request.</summary>
        public static void LogEgress(
            Uri requestUri,
            IReadOnlyList<Cookie> cookies,
            Uri topLevelDocumentUri,
            bool isTopLevelNavigation,
            string requestMethod)
        {
            if (!Enabled || cookies == null || cookies.Count == 0)
            {
                return;
            }

            try
            {
                var names = new List<string>(cookies.Count);
                foreach (var c in cookies)
                {
                    if (c == null) continue;
                    names.Add(c.Name);
                }

                var entry = new LogEntry
                {
                    Category = LogCategory.Storage,
                    Level = LogLevel.Info,
                    Message = $"[COOKIE-OUT] {requestUri?.Host}{requestUri?.AbsolutePath} count={cookies.Count} method={requestMethod}"
                };

                entry.WithData("event", "outbound-cookie-header");
                entry.WithData("requestHost", requestUri?.Host ?? string.Empty);
                entry.WithData("requestPath", requestUri?.AbsolutePath ?? string.Empty);
                entry.WithData("requestMethod", requestMethod ?? string.Empty);
                entry.WithData("cookieCount", cookies.Count);
                entry.WithData("cookieNames", names);
                entry.WithData("topLevelNavigation", isTopLevelNavigation);
                if (topLevelDocumentUri != null)
                {
                    entry.WithData("topLevelHost", topLevelDocumentUri.Host);
                }

                LogManager.Log(entry);
            }
            catch
            {
                // swallow
            }
        }

        private static string ExtractCookieName(string rawHeader)
        {
            if (string.IsNullOrEmpty(rawHeader)) return "<empty>";
            var eq = rawHeader.IndexOf('=');
            if (eq <= 0) return "<malformed>";
            return rawHeader.Substring(0, eq).Trim();
        }
    }
}
