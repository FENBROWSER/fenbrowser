using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.FenEngine.DOM
{
    /// <summary>
    /// Spec-aware in-memory cookie store used as the DocumentWrapper fallback
    /// when no CookieBridge is configured.
    /// Parses and honours: name=value, Path, Domain, Expires, Max-Age, Secure, HttpOnly, SameSite.
    /// </summary>
    public sealed class InMemoryCookieStore
    {
        private sealed class Entry
        {
            public string Name;
            public string Value;
            public string Path = "/";
            public string Domain;
            public DateTimeOffset? Expires;
            public bool Secure;
            public bool HttpOnly;
            public string SameSite = "Lax";

            public bool IsExpired() => Expires.HasValue && DateTimeOffset.UtcNow > Expires.Value;

            public bool PathMatches(Uri uri)
            {
                if (string.IsNullOrEmpty(Path) || Path == "/") return true;
                var p = uri?.AbsolutePath ?? "/";
                return p.StartsWith(Path, StringComparison.Ordinal);
            }

            public bool DomainMatches(Uri uri)
            {
                if (string.IsNullOrEmpty(Domain)) return true;
                var host = uri?.Host ?? "";
                var d = Domain.TrimStart('.');
                return host.Equals(d, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase);
            }
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        /// <summary>
        /// Parse a Set-Cookie header string and store or delete the cookie accordingly.
        /// </summary>
        public void SetCookie(string cookieStr, Uri requestUri)
        {
            if (string.IsNullOrWhiteSpace(cookieStr)) return;

            var segments = cookieStr.Split(';');
            if (segments.Length == 0) return;

            var nameValuePart = segments[0].Trim();
            var eqIdx = nameValuePart.IndexOf('=');
            string name, value;
            if (eqIdx > 0)
            {
                name = nameValuePart.Substring(0, eqIdx).Trim();
                value = nameValuePart.Substring(eqIdx + 1).Trim();
            }
            else
            {
                name = nameValuePart;
                value = "";
            }

            if (string.IsNullOrEmpty(name)) return;

            var entry = new Entry
            {
                Name = name,
                Value = value,
                Domain = requestUri?.Host ?? "",
                Path = GetDefaultCookiePath(requestUri?.AbsolutePath)
            };

            bool deleted = false;
            bool domainAttributeSpecified = false;

            for (int i = 1; i < segments.Length; i++)
            {
                var attr = segments[i].Trim();
                if (string.IsNullOrEmpty(attr)) continue;

                var attrEq = attr.IndexOf('=');
                var attrName = (attrEq > 0 ? attr.Substring(0, attrEq) : attr).Trim();
                var attrVal  = attrEq > 0 ? attr.Substring(attrEq + 1).Trim() : "";

                switch (attrName.ToLowerInvariant())
                {
                    case "path":
                        entry.Path = string.IsNullOrEmpty(attrVal) ? "/" : attrVal;
                        break;
                    case "domain":
                        // SECURITY: RFC 6265 §5.3 step 6 — the domain attribute value must
                        // domain-match the request URI's host. Silently ignore values that
                        // don't, rather than honouring them (prevents cross-site cookie injection).
                        var candidateDomain = attrVal.TrimStart('.').ToLowerInvariant();
                        if (!string.IsNullOrEmpty(candidateDomain))
                        {
                            var reqHost = requestUri?.Host?.ToLowerInvariant() ?? "";
                            bool isExact  = reqHost.Equals(candidateDomain, StringComparison.Ordinal);
                            bool isSuffix = reqHost.EndsWith("." + candidateDomain, StringComparison.Ordinal);
                            if (isExact || isSuffix)
                            {
                                entry.Domain = candidateDomain;
                                domainAttributeSpecified = true;
                            }
                            // else: silently ignore — entry keeps default domain (requestUri.Host)
                        }
                        break;
                    case "expires":
                        if (DateTimeOffset.TryParse(attrVal,
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var exp))
                        {
                            entry.Expires = exp;
                            if (exp < DateTimeOffset.UtcNow) deleted = true;
                        }
                        break;
                    case "max-age":
                        if (int.TryParse(attrVal, out var maxAge))
                        {
                            if (maxAge <= 0) { deleted = true; entry.Expires = DateTimeOffset.UtcNow.AddSeconds(-1); }
                            else entry.Expires = DateTimeOffset.UtcNow.AddSeconds(maxAge);
                        }
                        break;
                    case "secure":
                        entry.Secure = true;
                        break;
                    case "httponly":
                        entry.HttpOnly = true;
                        break;
                    case "samesite":
                        entry.SameSite = string.IsNullOrEmpty(attrVal) ? "Lax" : attrVal;
                        break;
                }
            }

            var isSecureRequest = string.Equals(requestUri?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (name.StartsWith("__Secure-", StringComparison.Ordinal))
            {
                if (!entry.Secure || !isSecureRequest)
                {
                    return;
                }
            }

            if (name.StartsWith("__Host-", StringComparison.Ordinal))
            {
                if (!entry.Secure || !isSecureRequest || domainAttributeSpecified || !string.Equals(entry.Path, "/", StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (deleted)
                _entries.Remove(name);
            else
                _entries[name] = entry;
        }

        /// <summary>
        /// Return the cookie header string for a given request URI.
        /// Filters by: not expired, path match, domain match, Secure only on HTTPS.
        /// </summary>
        /// <param name="requestUri">The URI making the request.</param>
        /// <param name="fromScript">
        /// When <c>true</c> (JavaScript context), HttpOnly cookies are excluded per spec.
        /// Pass <c>false</c> only when building the outgoing HTTP Cookie header.
        /// </param>
        public string GetCookieString(Uri requestUri, bool fromScript = false)
        {
            var isSecure = string.Equals(requestUri?.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            bool first = true;

            foreach (var entry in _entries.Values)
            {
                if (entry.IsExpired()) continue;
                if (entry.Secure && !isSecure) continue;
                if (!entry.PathMatches(requestUri)) continue;
                if (!entry.DomainMatches(requestUri)) continue;
                // SECURITY: HttpOnly cookies must never be readable from JavaScript (RFC 6265 §5.2)
                if (fromScript && entry.HttpOnly) continue;

                if (!first) sb.Append("; ");
                sb.Append(entry.Name).Append('=').Append(entry.Value);
                first = false;
            }

            return sb.ToString();
        }

        /// <summary>
        /// True if the store contains a non-expired cookie with the given name
        /// accessible from the given URI.
        /// </summary>
        /// <param name="fromScript">When <c>true</c>, HttpOnly cookies are invisible (JS context).</param>
        public bool Has(string name, Uri requestUri, bool fromScript = false)
        {
            if (!_entries.TryGetValue(name, out var e)) return false;
            if (e.IsExpired()) return false;
            if (!e.PathMatches(requestUri)) return false;
            if (!e.DomainMatches(requestUri)) return false;
            // SECURITY: HttpOnly cookies are invisible to JavaScript
            if (fromScript && e.HttpOnly) return false;
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static string GetDefaultCookiePath(string requestPath)
        {
            if (string.IsNullOrEmpty(requestPath) || requestPath == "/") return "/";
            var idx = requestPath.LastIndexOf('/');
            return idx <= 0 ? "/" : requestPath.Substring(0, idx + 1);
        }
    }
}
