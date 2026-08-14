// SpecRef: RFC6265bis cookie model and SameSite/Secure policy behavior
// CapabilityId: SECURITY-COOKIE-MODEL-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using FenBrowser.Core.Security;

namespace FenBrowser.Core.Storage
{
    /// <summary>
    /// Shared browser cookie jar used by both the network stack and document.cookie.
    /// Stores cookies in the partition-aware storage backend and applies SameSite,
    /// Secure, and third-party blocking rules when cookies are read or written.
    /// </summary>
    public sealed class BrowserCookieJar
    {
        private readonly StorageService _storage;

        public BrowserCookieJar(StorageService storage = null)
        {
            _storage = storage ?? new StorageService();
        }

        public StorageService Storage => _storage;

        public void ClearAll() => _storage.ClearAll();

        public string GetDocumentCookieString(Uri documentUri, Uri topLevelDocumentUri = null)
        {
            return BuildCookieString(
                documentUri,
                topLevelDocumentUri,
                includeHttpOnly: false,
                isTopLevelNavigation: false,
                requestMethod: HttpMethod.Get.Method);
        }

        public string GetRequestCookieHeader(
            Uri requestUri,
            Uri topLevelDocumentUri = null,
            bool isTopLevelNavigation = false,
            string requestMethod = "GET")
        {
            return BuildCookieString(
                requestUri,
                topLevelDocumentUri,
                includeHttpOnly: true,
                isTopLevelNavigation,
                requestMethod);
        }

        public IReadOnlyDictionary<string, string> Snapshot(Uri documentUri, Uri topLevelDocumentUri = null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cookie in SnapshotCookies(documentUri, topLevelDocumentUri, includeHttpOnly: false))
            {
                if (!map.ContainsKey(cookie.Name))
                    map[cookie.Name] = cookie.Value ?? string.Empty;
            }

            return map;
        }

        public IReadOnlyList<Cookie> SnapshotCookies(
            Uri documentUri,
            Uri topLevelDocumentUri = null,
            bool includeHttpOnly = false)
        {
            return GetCookies(
                documentUri,
                topLevelDocumentUri,
                includeHttpOnly,
                isTopLevelNavigation: false,
                requestMethod: HttpMethod.Get.Method);
        }

        public void SetDocumentCookie(
            Uri documentUri,
            string cookieString,
            Uri topLevelDocumentUri = null,
            bool blockThirdPartyCookies = false)
        {
            if (documentUri == null || string.IsNullOrWhiteSpace(cookieString))
                return;

            var context = BuildContext(documentUri, topLevelDocumentUri);
            if (blockThirdPartyCookies && context.IsThirdParty)
                return;

            if (!TryParseCookie(
                    cookieString,
                    documentUri,
                    context.PartitionKey,
                    fromScript: true,
                    out var cookie,
                    out var partitionKey))
            {
                CookieDiagnostics.LogIngressRejected(
                    documentUri,
                    cookieString,
                    "parse-failed-or-policy-blocked",
                    fromScript: true,
                    topLevelDocumentUri);
                return;
            }

            _storage.Cookies.Set(cookie, partitionKey);
            CookieDiagnostics.LogIngress(documentUri, cookie, topLevelDocumentUri, fromScript: true);
        }

        public void StoreResponseCookies(
            HttpResponseMessage response,
            Uri topLevelDocumentUri = null,
            bool blockThirdPartyCookies = false)
        {
            if (response?.Headers == null || response.RequestMessage?.RequestUri == null)
                return;

            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
                return;

            var responseUri = response.RequestMessage.RequestUri;
            var context = BuildContext(responseUri, topLevelDocumentUri);
            if (blockThirdPartyCookies && context.IsThirdParty)
            {
                foreach (var blockedHeader in setCookieValues)
                {
                    CookieDiagnostics.LogIngressRejected(
                        responseUri,
                        blockedHeader,
                        "third-party-blocked",
                        fromScript: false,
                        topLevelDocumentUri);
                }
                return;
            }

            foreach (var headerValue in setCookieValues)
            {
                if (!TryParseCookie(
                        headerValue,
                        responseUri,
                        context.PartitionKey,
                        fromScript: false,
                        out var cookie,
                        out var partitionKey))
                {
                    CookieDiagnostics.LogIngressRejected(
                        responseUri,
                        headerValue,
                        "parse-failed-or-policy-blocked",
                        fromScript: false,
                        topLevelDocumentUri);
                    continue;
                }

                _storage.Cookies.Set(cookie, partitionKey);
                CookieDiagnostics.LogIngress(responseUri, cookie, topLevelDocumentUri, fromScript: false);
            }
        }

        public void DeleteDocumentCookie(Uri documentUri, string name, Uri topLevelDocumentUri = null)
        {
            if (documentUri == null || string.IsNullOrWhiteSpace(name))
                return;

            var context = BuildContext(documentUri, topLevelDocumentUri);
            _storage.Cookies.DeleteByName(documentUri.Host, name, context.PartitionKey);
            _storage.Cookies.DeleteByName(documentUri.Host, name, null);
        }

        private string BuildCookieString(
            Uri requestUri,
            Uri topLevelDocumentUri,
            bool includeHttpOnly,
            bool isTopLevelNavigation,
            string requestMethod)
        {
            if (requestUri == null)
                return string.Empty;

            var matched = GetCookies(
                requestUri,
                topLevelDocumentUri,
                includeHttpOnly,
                isTopLevelNavigation,
                requestMethod);

            if (includeHttpOnly && matched is { Count: > 0 })
            {
                CookieDiagnostics.LogEgress(
                    requestUri,
                    matched,
                    topLevelDocumentUri,
                    isTopLevelNavigation,
                    requestMethod);
            }

            var sb = new StringBuilder();
            var first = true;
            foreach (var cookie in matched)
            {
                if (!first)
                    sb.Append("; ");

                first = false;
                sb.Append(cookie.Name).Append('=').Append(cookie.Value ?? string.Empty);
            }

            return sb.ToString();
        }

        private IReadOnlyList<Cookie> GetCookies(
            Uri requestUri,
            Uri topLevelDocumentUri,
            bool includeHttpOnly,
            bool isTopLevelNavigation,
            string requestMethod)
        {
            if (requestUri == null)
                return Array.Empty<Cookie>();

            var context = BuildContext(requestUri, topLevelDocumentUri);
            var candidates = _storage.Cookies.GetForUrl(
                requestUri.AbsoluteUri,
                context.PartitionKey,
                includeHttpOnly);

            var isSameSiteRequest = !context.IsThirdParty;
            var filtered = new List<Cookie>(candidates.Count);

            foreach (var cookie in candidates)
            {
                if (!SecurityChecks.ShouldSendCookie(
                        ToSameSiteString(cookie.SameSite),
                        isSameSiteRequest,
                        isTopLevelNavigation,
                        requestMethod))
                {
                    continue;
                }

                filtered.Add(cookie);
            }

            return filtered;
        }

        private static bool TryParseCookie(
            string cookieString,
            Uri requestUri,
            StoragePartitionKey partitionKey,
            bool fromScript,
            out Cookie cookie,
            out StoragePartitionKey? actualPartitionKey)
        {
            cookie = null;
            actualPartitionKey = null;

            if (requestUri == null || string.IsNullOrWhiteSpace(cookieString))
                return false;

            var segments = cookieString.Split(';');
            if (segments.Length == 0)
                return false;

            var nameValue = segments[0].Trim();
            var equalsIndex = nameValue.IndexOf('=');
            if (equalsIndex <= 0)
                return false;

            var name = nameValue[..equalsIndex].Trim();
            var value = nameValue[(equalsIndex + 1)..].Trim();
            if (string.IsNullOrEmpty(name))
                return false;

            var requestHost = requestUri.Host;
            var defaultPath = GetDefaultCookiePath(requestUri.AbsolutePath);
            var path = defaultPath;
            var secure = false;
            var httpOnly = false;
            var httpOnlyAttributeSpecified = false;
            var partitioned = false;
            var pathAttributeSpecified = false;
            string lastDomainAttribute = null;
            DateTimeOffset? expiresAttribute = null;
            DateTimeOffset? maxAgeExpiry = null;
            var sameSite = CookieSameSite.Unspecified;

            for (var i = 1; i < segments.Length; i++)
            {
                var segment = segments[i].Trim();
                if (string.IsNullOrEmpty(segment))
                    continue;

                var attributeEqualsIndex = segment.IndexOf('=');
                var attributeName = (attributeEqualsIndex >= 0 ? segment[..attributeEqualsIndex] : segment).Trim();
                var attributeValue = attributeEqualsIndex >= 0
                    ? segment[(attributeEqualsIndex + 1)..].Trim()
                    : string.Empty;

                switch (attributeName.ToLowerInvariant())
                {
                    case "path":
                        // RFC6265bis: a Path attribute whose value is empty or does
                        // not begin with '/' uses the computed default path.
                        pathAttributeSpecified = true;
                        path = !string.IsNullOrEmpty(attributeValue) && attributeValue[0] == '/'
                            ? attributeValue
                            : defaultPath;
                        break;

                    case "domain":
                    {
                        // An empty Domain cookie-av is ignored. For non-empty values,
                        // keep the last attribute and validate it after parsing all
                        // attributes, matching the cookie attribute-list algorithm.
                        if (!string.IsNullOrEmpty(attributeValue))
                        {
                            var candidate = attributeValue.TrimStart('.').ToLowerInvariant();
                            if (!IsAsciiCookieDomain(candidate))
                                return false;
                            lastDomainAttribute = candidate;
                        }
                        break;
                    }

                    case "expires":
                        if (DateTimeOffset.TryParse(
                                attributeValue,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                                out var parsedExpires))
                        {
                            expiresAttribute = parsedExpires;
                        }
                        break;

                    case "max-age":
                        if (long.TryParse(attributeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge))
                            maxAgeExpiry = ComputeMaxAgeExpiry(maxAge);
                        break;

                    case "secure":
                        secure = true;
                        break;

                    case "httponly":
                        httpOnlyAttributeSpecified = true;
                        httpOnly = true;
                        break;

                    case "samesite":
                        sameSite = ParseSameSite(attributeValue);
                        break;

                    case "partitioned":
                        partitioned = true;
                        break;
                }
            }

            var domain = requestHost;
            var domainAttributeSpecified = lastDomainAttribute != null;
            if (domainAttributeSpecified)
            {
                // Do not silently downgrade an invalid Domain attribute into a
                // host-only cookie. A Domain scope must include the origin host.
                if (string.IsNullOrEmpty(lastDomainAttribute) ||
                    !DomainMatches(requestHost, lastDomainAttribute))
                {
                    return false;
                }

                domain = lastDomainAttribute;
            }

            var isSecureRequest = string.Equals(
                requestUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase);

            // A non-HTTP API cannot create/overwrite an HttpOnly cookie merely by
            // specifying and then ignoring the HttpOnly attribute.
            if (fromScript && httpOnlyAttributeSpecified)
                return false;

            // Secure cookies are accepted only from a secure origin. Otherwise an
            // active HTTP attacker could plant state that is later sent over HTTPS.
            if (secure && !isSecureRequest)
                return false;

            if (sameSite == CookieSameSite.None && !secure)
                return false;

            if (partitioned && !secure)
                return false;

            if (name.StartsWith("__Secure-", StringComparison.Ordinal) && (!secure || !isSecureRequest))
                return false;

            if (name.StartsWith("__Host-", StringComparison.Ordinal) &&
                (!secure ||
                 !isSecureRequest ||
                 domainAttributeSpecified ||
                 !pathAttributeSpecified ||
                 !string.Equals(path, "/", StringComparison.Ordinal)))
            {
                return false;
            }

            actualPartitionKey = partitioned ? partitionKey : null;
            cookie = new Cookie
            {
                Name = name,
                Value = value,
                Domain = domain,
                Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
                HostOnly = !domainAttributeSpecified,
                Secure = secure,
                HttpOnly = httpOnly,
                SameSite = sameSite,
                // Max-Age takes precedence over Expires regardless of attribute order.
                Expires = maxAgeExpiry ?? expiresAttribute,
                PartitionKey = actualPartitionKey
            };

            return true;
        }

        private static DateTimeOffset ComputeMaxAgeExpiry(long maxAge)
        {
            if (maxAge <= 0)
                return DateTimeOffset.MinValue;

            var now = DateTimeOffset.UtcNow;
            var maxSeconds = (DateTimeOffset.MaxValue - now).TotalSeconds;
            if (maxAge >= maxSeconds)
                return DateTimeOffset.MaxValue;

            return now.AddSeconds(maxAge);
        }

        private static bool IsAsciiCookieDomain(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c > 0x7F || char.IsControl(c) || char.IsWhiteSpace(c))
                    return false;
            }

            return true;
        }

        private static CookieSameSite ParseSameSite(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "strict" => CookieSameSite.Strict,
                "none" => CookieSameSite.None,
                "lax" => CookieSameSite.Lax,
                _ => CookieSameSite.Lax
            };
        }

        private static string ToSameSiteString(CookieSameSite sameSite)
        {
            return sameSite switch
            {
                CookieSameSite.Strict => "strict",
                CookieSameSite.None => "none",
                CookieSameSite.Unspecified => "lax",
                _ => "lax"
            };
        }

        private static string GetDefaultCookiePath(string requestPath)
        {
            if (string.IsNullOrEmpty(requestPath) || requestPath == "/")
                return "/";

            var index = requestPath.LastIndexOf('/');
            return index <= 0 ? "/" : requestPath[..index];
        }

        private static bool DomainMatches(string host, string cookieDomain)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(cookieDomain))
                return false;

            return host.Equals(cookieDomain, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
        }

        private static CookieContext BuildContext(Uri requestUri, Uri topLevelDocumentUri)
        {
            var effectiveTopLevel = topLevelDocumentUri ?? requestUri;
            var partitionKey = StoragePartitionKeyFactory.Compute(
                effectiveTopLevel?.AbsoluteUri,
                requestUri?.AbsoluteUri);

            return new CookieContext(partitionKey);
        }

        private readonly struct CookieContext
        {
            public CookieContext(StoragePartitionKey partitionKey)
            {
                PartitionKey = partitionKey;
            }

            public StoragePartitionKey PartitionKey { get; }
            public bool IsThirdParty => PartitionKey.IsThirdParty;
        }
    }
}
