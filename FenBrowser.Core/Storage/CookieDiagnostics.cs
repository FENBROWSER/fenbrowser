// SpecRef: RFC6265 (Set-Cookie / Cookie header semantics)
// CapabilityId: DIAG-COOKIES-01
// Determinism: strict
// FallbackPolicy: silent (diagnostics must never throw into the network path)
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Storage
{
    /// <summary>
    /// Structured, PII-safe diagnostics for cookie ingress (Set-Cookie) and
    /// egress (outbound Cookie header).
    ///
    /// Cookie values are NEVER logged in plaintext. Only:
    ///   - cookie name
    ///   - value length (bytes, UTF-8)
    ///   - 8-hex-char prefix of SHA-256(value) for correlation across requests
    ///   - attributes: Domain, Path, Secure, HttpOnly, SameSite, Expires
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
                    Message = $"[COOKIE-IN] {cookie.Name}@{cookie.Domain} len={ValueLength(cookie.Value)} tag={ValueTag(cookie.Value)} src={(fromScript ? "script" : "header")}"
                };

                entry.WithData("event", "set-cookie");
                entry.WithData("source", fromScript ? "document.cookie" : "http-response");
                entry.WithData("name", cookie.Name);
                entry.WithData("valueLength", ValueLength(cookie.Value));
                entry.WithData("valueTag", ValueTag(cookie.Value));
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
            bool fromScript)
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
                entry.WithData("reason", reason ?? "unknown");
                entry.WithData("rawLength", rawHeader?.Length ?? 0);
                if (responseUri != null)
                {
                    entry.WithData("responseHost", responseUri.Host);
                    entry.WithData("responsePath", responseUri.AbsolutePath);
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
                var tags = new List<string>(cookies.Count);
                long totalBytes = 0;
                foreach (var c in cookies)
                {
                    if (c == null) continue;
                    names.Add(c.Name);
                    tags.Add($"{c.Name}:{ValueLength(c.Value)}:{ValueTag(c.Value)}");
                    totalBytes += (c.Name?.Length ?? 0) + ValueLength(c.Value) + 1;
                }

                var entry = new LogEntry
                {
                    Category = LogCategory.Storage,
                    Level = LogLevel.Info,
                    Message = $"[COOKIE-OUT] {requestUri?.Host}{requestUri?.AbsolutePath} count={cookies.Count} bytes~{totalBytes} method={requestMethod}"
                };

                entry.WithData("event", "outbound-cookie-header");
                entry.WithData("requestHost", requestUri?.Host ?? string.Empty);
                entry.WithData("requestPath", requestUri?.AbsolutePath ?? string.Empty);
                entry.WithData("requestMethod", requestMethod ?? string.Empty);
                entry.WithData("cookieCount", cookies.Count);
                entry.WithData("approxHeaderBytes", totalBytes);
                entry.WithData("cookieNames", names);
                entry.WithData("cookieTags", tags);
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

        private static int ValueLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            return Encoding.UTF8.GetByteCount(value);
        }

        private static string ValueTag(string value)
        {
            if (string.IsNullOrEmpty(value)) return "0";
            try
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++)
                {
                    sb.Append(bytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
            catch
            {
                return "?";
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
