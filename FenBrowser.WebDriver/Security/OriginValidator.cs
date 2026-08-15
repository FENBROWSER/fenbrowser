// =============================================================================
// OriginValidator.cs
// WebDriver Security - Origin Validation
//
// PURPOSE: Validates request origins to prevent browser-originated CSRF.
// SECURITY: Whitelist-based origin checking, localhost enforcement.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Validates request origins for security.
    /// </summary>
    public class OriginValidator
    {
        private static readonly IdnMapping Idn = new();

        private readonly HashSet<string> _allowedOrigins = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _allowedOriginsLock = new();
        private readonly bool _allowLocalhostOnly;

        public OriginValidator(bool allowLocalhostOnly = true)
        {
            _allowLocalhostOnly = allowLocalhostOnly;

            _allowedOrigins.Add("localhost");
            _allowedOrigins.Add("127.0.0.1");
            _allowedOrigins.Add("::1");
        }

        /// <summary>
        /// Add an allowed host. The allowlist is shared by request-handler threads,
        /// so mutation and lookup are serialized rather than racing a HashSet reader.
        /// </summary>
        public void AllowOrigin(string origin)
        {
            var normalized = NormalizeAllowedHost(origin);
            if (normalized.Length == 0)
                return;

            lock (_allowedOriginsLock)
            {
                _allowedOrigins.Add(normalized);
            }
        }

        /// <summary>
        /// Validate that the transport peer is allowed.
        /// </summary>
        public bool ValidateOrigin(IPEndPoint remoteEndpoint)
        {
            if (remoteEndpoint == null)
                return false;

            var address = remoteEndpoint.Address;
            if (IPAddress.IsLoopback(address))
                return true;

            if (_allowLocalhostOnly)
                return false;

            return IsAllowedHost(address.ToString());
        }

        /// <summary>
        /// Validate the HTTP Origin header if present. An Origin header is a
        /// serialized origin (scheme + host + optional port), not an arbitrary URL.
        /// </summary>
        public bool ValidateOriginHeader(string originHeader)
        {
            if (string.IsNullOrEmpty(originHeader))
                return true; // Non-browser WebDriver clients normally omit Origin.

            var allowBrowserOrigins = string.Equals(
                Environment.GetEnvironmentVariable("FEN_WEBDRIVER_ALLOW_BROWSER_ORIGINS"),
                "1",
                StringComparison.OrdinalIgnoreCase);
            if (!allowBrowserOrigins)
                return false;

            if (!Uri.TryCreate(originHeader, UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // The Origin header cannot contain credentials, query, fragment, or a
            // non-root path. Accepting a full URL here broadens the parser surface
            // and makes validation semantics depend on irrelevant URL components.
            if (!string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
            {
                return false;
            }

            var host = NormalizeAllowedHost(uri.IdnHost);
            if (host.Length == 0)
                return false;

            if (_allowLocalhostOnly)
            {
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                    return true;

                return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
            }

            return IsAllowedHost(host);
        }

        private bool IsAllowedHost(string host)
        {
            var normalized = NormalizeAllowedHost(host);
            if (normalized.Length == 0)
                return false;

            lock (_allowedOriginsLock)
            {
                return _allowedOrigins.Contains(normalized);
            }
        }

        private static string NormalizeAllowedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return string.Empty;

            var normalized = host.Trim().TrimEnd('.').Trim('[', ']');
            if (normalized.Length == 0 ||
                normalized.IndexOfAny(new[] { '/', '\\', '@', '?', '#' }) >= 0)
            {
                return string.Empty;
            }

            if (IPAddress.TryParse(normalized, out var ip))
                return ip.ToString();

            try
            {
                return Idn.GetAscii(normalized).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }
}
