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

        public void ClearAll()
        {
            _storage.ClearAll();
        }

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
            foreach (var cookie in GetCookies(
                         documentUri,
                         topLevelDocumentUri,
                         includeHttpOnly: false,
                         isTopLevelNavigation: false,
                         requestMethod: HttpMethod.Get.Method))
            {
                if (!map.ContainsKey(cookie.Name))
                {
                    map[cookie.Name] = cookie.Value ?? string.Empty;
                }
            }

            return map;
        }

        public void SetDocumentCookie(
            Uri documentUri,
            string cookieString,
            Uri topLevelDocumentUri = null,
            bool blockThirdPartyCookies = false)
        {
            if (documentUri == null || string.IsNullOrWhiteSpace(cookieString))
            {
                return;
            }

            var context = BuildContext(documentUri, topLevelDocumentUri);
            if (blockThirdPartyCookies && context.IsThirdParty)
            {
                return;
            }

            if (!TryParseCookie(
                    cookieString,
                    documentUri,
                    context.PartitionKey,
                    fromScript: true,
                    out var cookie,
                    out var partitionKey))
            {
                return;
            }

            _storage.Cookies.Set(cookie, partitionKey);
        }

        public void StoreResponseCookies(
            HttpResponseMessage response,
            Uri topLevelDocumentUri = null,
            bool blockThirdPartyCookies = false)
        {
            if (response?.Headers == null || response.RequestMessage?.RequestUri == null)
            {
                return;
            }

            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
            {
                return;
            }

            var responseUri = response.RequestMessage.RequestUri;
            var context = BuildContext(responseUri, topLevelDocumentUri);
            if (blockThirdPartyCookies && context.IsThirdParty)
            {
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
                    continue;
                }

                _storage.Cookies.Set(cookie, partitionKey);
            }
        }

        public void DeleteDocumentCookie(Uri documentUri, string name, Uri topLevelDocumentUri = null)
        {
            if (documentUri == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var context = BuildContext(documentUri, topLevelDocumentUri);
            var expiredCookie = new Cookie
            {
                Name = name,
                Value = string.Empty,
                Domain = documentUri.Host,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddSeconds(-1),
                SameSite = CookieSameSite.Lax
            };

            _storage.Cookies.Set(expiredCookie, context.PartitionKey);
            _storage.Cookies.Set(expiredCookie, null);
        }

        private string BuildCookieString(
            Uri requestUri,
            Uri topLevelDocumentUri,
            bool includeHttpOnly,
            bool isTopLevelNavigation,
            string requestMethod)
        {
            if (requestUri == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            bool first = true;

            foreach (var cookie in GetCookies(
                         requestUri,
                         topLevelDocumentUri,
                         includeHttpOnly,
                         isTopLevelNavigation,
                         requestMethod))
            {
                if (!first)
                {
                    sb.Append("; ");
                }

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
            {
                return Array.Empty<Cookie>();
            }

            var context = BuildContext(requestUri, topLevelDocumentUri);
            var candidates = _storage.Cookies.GetForUrl(
                requestUri.AbsoluteUri,
                context.PartitionKey,
                includeHttpOnly);

            bool isSameSiteRequest = !context.IsThirdParty;
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
            {
                return false;
            }

            var segments = cookieString.Split(';');
            if (segments.Length == 0)
            {
                return false;
            }

            var nameValue = segments[0].Trim();
            int equalsIndex = nameValue.IndexOf('=');
            if (equalsIndex <= 0)
            {
                return false;
            }

            string name = nameValue[..equalsIndex].Trim();
            string value = nameValue[(equalsIndex + 1)..].Trim();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string domain = requestUri.Host;
            string path = GetDefaultCookiePath(requestUri.AbsolutePath);
            bool secure = false;
            bool httpOnly = false;
            bool partitioned = false;
            bool domainAttributeSpecified = false;
            DateTimeOffset? expires = null;
            CookieSameSite sameSite = CookieSameSite.Lax;

            for (int i = 1; i < segments.Length; i++)
            {
                var segment = segments[i].Trim();
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                int attributeEqualsIndex = segment.IndexOf('=');
                string attributeName = (attributeEqualsIndex >= 0 ? segment[..attributeEqualsIndex] : segment).Trim();
                string attributeValue = attributeEqualsIndex >= 0 ? segment[(attributeEqualsIndex + 1)..].Trim() : string.Empty;

                switch (attributeName.ToLowerInvariant())
                {
                    case "path":
                        path = string.IsNullOrWhiteSpace(attributeValue) ? "/" : attributeValue;
                        break;
                    case "domain":
                        var candidateDomain = attributeValue.Trim().TrimStart('.').ToLowerInvariant();
                        if (!string.IsNullOrEmpty(candidateDomain) && DomainMatches(requestUri.Host, candidateDomain))
                        {
                            domain = candidateDomain;
                            domainAttributeSpecified = true;
                        }
                        break;
                    case "expires":
                        if (DateTimeOffset.TryParse(
                                attributeValue,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                                out var parsedExpires))
                        {
                            expires = parsedExpires;
                        }
                        break;
                    case "max-age":
                        if (long.TryParse(attributeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge))
                        {
                            expires = maxAge <= 0
                                ? DateTimeOffset.UtcNow.AddSeconds(-1)
                                : DateTimeOffset.UtcNow.AddSeconds(maxAge);
                        }
                        break;
                    case "secure":
                        secure = true;
                        break;
                    case "httponly":
                        if (!fromScript)
                        {
                            httpOnly = true;
                        }
                        break;
                    case "samesite":
                        sameSite = ParseSameSite(attributeValue);
                        break;
                    case "partitioned":
                        partitioned = true;
                        break;
                }
            }

            bool isSecureRequest = string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            if (sameSite == CookieSameSite.None && !secure)
            {
                return false;
            }

            if (partitioned && !secure)
            {
                return false;
            }

            if (name.StartsWith("__Secure-", StringComparison.Ordinal) && (!secure || !isSecureRequest))
            {
                return false;
            }

            if (name.StartsWith("__Host-", StringComparison.Ordinal) &&
                (!secure || !isSecureRequest || domainAttributeSpecified || !string.Equals(path, "/", StringComparison.Ordinal)))
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
                Secure = secure,
                HttpOnly = httpOnly,
                SameSite = sameSite,
                Expires = expires,
                PartitionKey = actualPartitionKey
            };

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
            {
                return "/";
            }

            int index = requestPath.LastIndexOf('/');
            return index <= 0 ? "/" : requestPath[..(index + 1)];
        }

        private static bool DomainMatches(string host, string cookieDomain)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(cookieDomain))
            {
                return false;
            }

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
