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
    /// Secure, HttpOnly, and third-party blocking rules when cookies are read or written.
    /// </summary>
    public sealed class BrowserCookieJar
    {
        private const int MaxCookieLineChars = 16 * 1024;
        private const int MaxCookieAttributes = 64;
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

            // document.cookie is a non-HTTP API. It must not overwrite or delete an
            // existing HttpOnly cookie simply by omitting the HttpOnly attribute from
            // the new string. Query the exact storage identity using a synthetic HTTPS
            // probe so Secure+HttpOnly cookies are protected even when the current
            // document itself is loaded over HTTP.
            if (WouldOverwriteHttpOnlyCookie(documentUri, context.PartitionKey, cookie, partitionKey))
            {
                CookieDiagnostics.LogIngressRejected(
                    documentUri,
                    cookieString,
                    "httponly-overwrite-blocked",
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
            var canonicalHost = documentUri.IdnHost;
            _storage.Cookies.DeleteByName(canonicalHost, name, context.PartitionKey);
            _storage.Cookies.DeleteByName(canonicalHost, name, null);
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
                        requestMethod,
                        cookie.CreationTime))
                {
                    continue;
                }

                filtered.Add(cookie);
            }

            return filtered;
        }

        private bool WouldOverwriteHttpOnlyCookie(
            Uri documentUri,
            StoragePartitionKey contextPartitionKey,
            Cookie candidate,
            StoragePartitionKey? candidatePartitionKey)
        {
            if (documentUri == null || candidate == null || string.IsNullOrEmpty(candidate.Name))
                return false;

            Uri probeUri;
            try
            {
                var builder = new UriBuilder(documentUri)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Port = -1,
                    Path = string.IsNullOrEmpty(candidate.Path) ? "/" : candidate.Path,
                    Query = string.Empty,
                    Fragment = string.Empty
                };
                probeUri = builder.Uri;
            }
            catch
            {
                return true; // fail closed for a malformed non-HTTP cookie probe
            }

            var existingCookies = _storage.Cookies.GetForUrl(
                probeUri.AbsoluteUri,
                contextPartitionKey,
                includeHttpOnly: true);

            foreach (var existing in existingCookies)
            {
                if (!existing.HttpOnly) continue;
                if (!string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal)) continue;
                if (!string.Equals(existing.Domain, candidate.Domain, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(existing.Path, candidate.Path, StringComparison.Ordinal)) continue;
                if (!Nullable.Equals(existing.PartitionKey, candidatePartitionKey)) continue;

                // The store key is partition/domain/name/path; this write would replace
                // the protected cookie regardless of HostOnly metadata.
                return true;
            }

            return false;
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

            if (requestUri == null ||
                !requestUri.IsAbsoluteUri ||
                (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(requestUri.Scheme, Uri.UÉ¥M¡•µ•!ÑÑÁÌ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…±%¹½É•…Í”¤¤ñğ(€€€€€€€€€€€€€€€ÍÑÉ¥¹œ¹%Í9Õ±±=É]¡¥Ñ•MÁ…”¡½½­¥•MÑÉ¥¹œ¤¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô((€€€€€€€€€€€¥˜€¡½½­¥•MÑÉ¥¹œ¹1•¹Ñ €ø5…á½½­¥•1¥¹•¡…ÉÌ¤(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€Ù…È…ÑÑÉ¥‰ÕÑ•M•Á…É…Ñ½ÉÌ€ô€Àì(€€€€€€€€€€€™½È€¡Ù…È¤€ô€Àì¤€ğ½½­¥•MÑÉ¥¹œ¹1•¹Ñ ì¤¬¬¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥˜€¡½½­¥•MÑÉ¥¹m¥t€ôô€œìœ€˜˜€¬­…ÑÑÉ¥‰ÕÑ•M•Á…É…Ñ½ÉÌ€ø5…á½½­¥•ÑÑÉ¥‰ÕÑ•Ì¤(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…ÈÍ•µ•¹ÑÌ€ô½½­¥•MÑÉ¥¹œ¹MÁ±¥Ğ œìœ¤ì(€€€€€€€€€€€¥˜€¡Í•µ•¹ÑÌ¹1•¹Ñ €ôô€À¤(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€Ù…È¹…µ•Y…±Õ”€ôÍ•µ•¹ÑÍlÁt¹QÉ¥´ ¤ì(€€€€€€€€€€€Ù…È•ÅÕ…±Í%¹‘•à€ô¹…µ•Y…±Õ”¹%¹‘•á=˜ œôœ¤ì(€€€€€€€€€€€¥˜€¡•ÅÕ…±Í%¹‘•à€ğô€À¤(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€Ù…È¹…µ”€ô¹…µ•Y…±Õ•l¸¹•ÅÕ…±Í%¹‘•át¹QÉ¥´ ¤ì(€€€€€€€€€€€Ù…ÈÙ…±Õ”€ô¹…µ•Y…±Õ•l¡•ÅÕ…±Í%¹‘•à€¬€Ä¤¸¹t¹QÉ¥´ ¤ì(€€€€€€€€€€€¥˜€ …%Í½½­¥•9…µ”¡¹…µ”¤ñğ€…%ÍM…™•½½­¥•Y…±Õ”¡Ù…±Õ”¤¤(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€Ù…ÈÉ•ÅÕ•ÍÑ!½ÍĞ€ôM¥Ñ•%‘•¹Ñ¥ÑåM•ÉÙ¥”¹•™…Õ±Ğ¹…¹½¹¥…±¥é•!½ÍĞ¡É•ÅÕ•ÍÑUÉ¤¹%‘¹!½ÍĞ¤ì(€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=ÉµÁÑä¡É•ÅÕ•ÍÑ!½ÍĞ¤¤(€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€Ù…È‘•™…Õ±ÑA…Ñ €ô•Ñ•™…Õ±Ñ½½­¥•A…Ñ ¡É•ÅÕ•ÍÑUÉ¤¹‰Í½±ÕÑ•A…Ñ ¤ì(€€€€€€€€€€€Ù…ÈÁ…Ñ €ô‘•™…Õ±ÑA…Ñ ì(€€€€€€€€€€€Ù…ÈÍ•ÕÉ”€ô™…±Í”ì(€€€€€€€€€€€Ù…È¡ÑÑÁ=¹±ä€ô™…±Í”ì(€€€€€€€€€€€Ù…È¡ÑÑÁ=¹±åÑÑÉ¥‰ÕÑ•MÁ•¥™¥•€ô™…±Í”ì(€€€€€€€€€€€Ù…ÈÁ…ÉÑ¥Ñ¥½¹•€ô™…±Í”ì(€€€€€€€€€€€Ù…ÈÁ…Ñ¡ÑÑÉ¥‰ÕÑ•MÁ•¥™¥•€ô™…±Í”ì(€€€€€€€€€€€ÍÑÉ¥¹œ±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”€ô¹Õ±°ì(€€€€€€€€€€€…Ñ•Q¥µ•=™™Í•Ğü•áÁ¥É•ÍÑÑÉ¥‰ÕÑ”€ô¹Õ±°ì(€€€€€€€€€€€…Ñ•Q¥µ•=™™Í•Ğüµ…á•áÁ¥Éä€ô¹Õ±°ì(€€€€€€€€€€€Ù…ÈÍ…µ•M¥Ñ”€ô½½­¥•M…µ•M¥Ñ”¹U¹ÍÁ•¥™¥•ì((€€€€€€€€€€€™½È€¡Ù…È¤€ô€Äì¤€ğÍ•µ•¹ÑÌ¹1•¹Ñ ì¤¬¬¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€Ù…ÈÍ•µ•¹Ğ€ôÍ•µ•¹ÑÍm¥t¹QÉ¥´ ¤ì(€€€€€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=ÉµÁÑä¡Í•µ•¹Ğ¤¤(€€€€€€€€€€€€€€€€€€€½¹Ñ¥¹Õ”ì((€€€€€€€€€€€€€€€Ù…È…ÑÑÉ¥‰ÕÑ•ÅÕ…±Í%¹‘•à€ôÍ•µ•¹Ğ¹%¹‘•á=˜ œôœ¤ì(€€€€€€€€€€€€€€€Ù…È…ÑÑÉ¥‰ÕÑ•9…µ”€ô€¡…ÑÑÉ¥‰ÕÑ•ÅÕ…±Í%¹‘•à€øô€À€üÍ•µ•¹Ñl¸¹…ÑÑÉ¥‰ÕÑ•ÅÕ…±Í%¹‘•át€èÍ•µ•¹Ğ¤¹QÉ¥´ ¤ì(€€€€€€€€€€€€€€€Ù…È…ÑÑÉ¥‰ÕÑ•Y…±Õ”€ô…ÑÑÉ¥‰ÕÑ•ÅÕ…±Í%¹‘•à€øô€À(€€€€€€€€€€€€€€€€€€€€üÍ•µ•¹Ñl¡…ÑÑÉ¥‰ÕÑ•ÅÕ…±Í%¹‘•à€¬€Ä¤¸¹t¹QÉ¥´ ¤(€€€€€€€€€€€€€€€€€€€€èÍÑÉ¥¹œ¹µÁÑäì((€€€€€€€€€€€€€€€¥˜€ …%Í½½­¥•ÑÑÉ¥‰ÕÑ•M…™”¡…ÑÑÉ¥‰ÕÑ•9…µ”°…ÑÑÉ¥‰ÕÑ•Y…±Õ”¤¤(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€€€€€Íİ¥Ñ €¡…ÑÑÉ¥‰ÕÑ•9…µ”¹Q½1½İ•É%¹Ù…É¥…¹Ğ ¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€…Í”€‰Á…Ñ ˆè(€€€€€€€€€€€€€€€€€€€€€€€€¼¼IØÈØÕ‰¥Ìè„A…Ñ …ÑÑÉ¥‰ÕÑ”İ¡½Í”Ù…±Õ”¥Ì•µÁÑä½È‘½•Ì¹½Ğ(€€€€€€€€€€€€€€€€€€€€€€€€¼¼‰•¥¸İ¥Ñ €œ¼œÕÍ•ÌÑ¡”½µÁÕÑ•‘•™…Õ±ĞÁ…Ñ ¸(€€€€€€€€€€€€€€€€€€€€€€€Á…Ñ¡ÑÑÉ¥‰ÕÑ•MÁ•¥™¥•€ôÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€€€€€Á…Ñ €ô€…ÍÑÉ¥¹œ¹%Í9Õ±±=ÉµÁÑä¡…ÑÑÉ¥‰ÕÑ•Y…±Õ”¤€˜˜…ÑÑÉ¥‰ÕÑ•Y…±Õ•lÁt€ôô€œ¼œ(€€€€€€€€€€€€€€€€€€€€€€€€€€€€ü…ÑÑÉ¥‰ÕÑ•Y…±Õ”(€€€€€€€€€€€€€€€€€€€€€€€€€€€€è‘•™…Õ±ÑA…Ñ ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì((€€€€€€€€€€€€€€€€€€€…Í”€‰‘½µ…¥¸ˆè(€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€¼¼¸•µÁÑä½µ…¥¸½½­¥”µ…Ø¥Ì¥¹½É•¸½È¹½¸µ•µÁÑäÙ…±Õ•Ì°(€€€€€€€€€€€€€€€€€€€€€€€€¼¼­••ÀÑ¡”±…ÍĞ…ÑÑÉ¥‰ÕÑ”…¹Ù…±¥‘…Ñ”¥Ğ…™Ñ•ÈÁ…ÉÍ¥¹œ…±°(€€€€€€€€€€€€€€€€€€€€€€€€¼¼…ÑÑÉ¥‰ÕÑ•Ì°µ…Ñ¡¥¹œÑ¡”½½­¥”…ÑÑÉ¥‰ÕÑ”µ±¥ÍĞ…±½É¥Ñ¡´¸(€€€€€€€€€€€€€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹%Í9Õ±±=ÉµÁÑä¡…ÑÑÉ¥‰ÕÑ•Y…±Õ”¤¤(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€¥˜€ …QÉå…¹½¹¥…±¥é•½½­¥•½µ…¥¸¡…ÑÑÉ¥‰ÕÑ•Y…±Õ”°½ÕĞÙ…È…¹‘¥‘…Ñ”¤¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€€€€€€€€€€€€€€€±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”€ô…¹‘¥‘…Ñ”ì(€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€€€€€€€€€ô((€€€€€€€€€€€€€€€€€€€…Í”€‰•áÁ¥É•Ìˆè(€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡…Ñ•Q¥µ•=™™Í•Ğ¹QÉåA…ÉÍ” (€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€…ÑÑÉ¥‰ÕÑ•Y…±Õ”°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€€Õ±ÑÕÉ•%¹™¼¹%¹Ù…É¥…¹ÑÕ±ÑÕÉ”°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€…Ñ•Q¥µ•MÑå±•Ì¹±±½İ]¡¥Ñ•MÁ…•Ìğ…Ñ•Q¥µ•MÑå±•Ì¹ÍÍÕµ•U¹¥Ù•ÉÍ…°°(€€€€€€€€€€€€€€€€€€€€€€€€€€€€€½ÕĞÙ…ÈÁ…ÉÍ•‘áÁ¥É•Ì¤¤(€€€€€€€€€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€€€€€€€€€•áÁ¥É•ÍÑÑÉ¥‰ÕÑ”€ôÁ…ÉÍ•‘áÁ¥É•Ìì(€€€€€€€€€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì((€€€€€€€€€€€€€€€€€€€…Í”€‰µ…àµ…”ˆè(€€€€€€€€€€€€€€€€€€€€€€€¥˜€¡±½¹œ¹ÑÉåA…ÉÍ”¡…ÑÑÉ¥‰ÕÑ•Y…±Õ”°9Õµ‰•ÉMÑå±•Ì¹%¹Ñ••È°Õ±ÑÕÉ•%¹™¼¹%¹Ù…É¥…¹ÑÕ±ÑÕÉ”°½ÕĞÙ…Èµ…á”¤¤(€€€€€€€€€€€€€€€€€€€€€€€€€€€µ…á•áÁ¥Éä€ô½µÁÕÑ•5…á•áÁ¥Éä¡µ…á”¤ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì((€€€€€€€€€€€€€€€€€€€…Í”€‰Í•ÕÉ”ˆè(€€€€€€€€€€€€€€€€€€€€€€€Í•ÕÉ”€ôÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì((€€€€€€€€€€€€€€€€€€€…Í”€‰¡ÑÑÁ½¹±äˆè(€€€€€€€€€€€€€€€€€€€€€€€¡ÑÑÁ=¹±åÑÑÉ¥‰ÕÑ•MÁ•¥™¥•€ôÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€€€€€¡ÑÑÁ=¹±ä€ôÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì((€€€€€€€€€€€€€€€€€€€…Í”€‰Í…µ•Í¥Ñ”ˆè(€€€€€€€€€€€€€€€€€€€€€€€Í…µ•M¥Ñ”€ôA…ÉÍ•M…µ•M¥Ñ”¡…ÑÑÉ¥‰ÕÑ•Y…±Õ”¤ì(€€€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì((€€€€€€€€€€€€€€€€€€€…Í”€‰Á…ÉÑ¥Ñ¥½¹•ˆè(€€€€€€€€€€€€€€€€€€€€€€€Á…ÉÑ¥Ñ¥½¹•€ôÑÉÕ”ì(€€€€€€€€€€€€€€€€€€€€€‰É•…¬ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È‘½µ…¥¸€ôÉ•ÅÕ•ÍÑ!½ÍĞì(€€€€€€€€€€€Ù…È‘½µ…¥¹ÑÑÉ¥‰ÕÑ•MÁ•¥™¥•€ô±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”€„ô¹Õ±°ì(€€€€€€€€€€€¥˜€¡‘½µ…¥¹ÑÑÉ¥‰ÕÑ•MÁ•¥™¥•¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€¥˜€¡ÍÑÉ¥¹œ¹%Í9Õ±±=ÉµÁÑä¡±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”¤¤(€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€€€€€€¼¼IØÈØÕ‰¥ÌÁÕ‰±¥ŒµÍÕ™™¥à¡…¹‘±¥¹œèÉ•©•Ğ½µ…¥¸õÁÕ‰±¥ŒµÍÕ™™¥à(€€€€€€€€€€€€€€€€¼¼Õ¹±•ÍÌ¥Ğ•á…Ñ±ä•ÅÕ…±ÌÑ¡”É•ÅÕ•ÍĞ¡½ÍĞ°¥¸İ¡¥ …Í”Ñ¡”(€€€€€€€€€€€€€€€€¼¼½µ…¥¸…ÑÑÉ¥‰ÕÑ”¥Ì¥¹½É•…¹Ñ¡”½½­¥”É•µ…¥¹Ì¡½ÍĞµ½¹±ä¸(€€€€€€€€€€€€€€€¥˜€¡M¥Ñ•%‘•¹Ñ¥ÑåM•ÉÙ¥”¹•™…Õ±Ğ¹%ÍAÕ‰±¥MÕ™™¥à¡±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”¤¤(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€¥˜€ …ÍÑÉ¥¹œ¹ÅÕ…±Ì¡±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”°É•ÅÕ•ÍÑ!½ÍĞ°MÑÉ¥¹½µÁ…É¥Í½¸¹=É‘¥¹…°¤¤(€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì((€€€€€€€€€€€€€€€€€€€±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”€ô¹Õ±°ì(€€€€€€€€€€€€€€€€€€€‘½µ…¥¹ÑÑÉ¥‰ÕÑ•MÁ•¥™¥•€ô™…±Í”ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€€€€€•±Í”(€€€€€€€€€€€€€€€ì(€€€€€€€€€€€€€€€€€€€¥˜€ …½µ…¥¹5…Ñ¡•Ì¡É•ÅÕ•ÍÑ!½ÍĞ°±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”¤¤(€€€€€€€€€€€€€€€€€€€€€€€É•ÑÕÉ¸™…±Í”ì(€€€€€€€€€€€€€€€€€€€‘½µ…¥¸€ô±…ÍÑ½µ…¥¹ÑÑÉ¥‰ÕÑ”ì(€€€€€€€€€€€€€€€ô(€€€€€€€€€€€ô((€€€€€€€€€€€Ù…È¥ÍM•ÕÉ•I•ÅÕ•ÍĞ€ôÍÑÉ¥¹œ¹ÅÕ…±Ì (€€€€€€€€€€€€€€€É•ÅÕ•ÍÑUÉ¤¹M¡•µ”°(€€€€€€€€€€€€€€€UÉ¤¹W&•66†VÖT‡GG2À¢7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“° ¢òòæöâÔ…EE’6ææ÷B7&VFRö÷fW'w&—FRâ‡GGöæÇ’6öö¶–RÖW&VÇ’'¢òò7V6–g––æræBF†Vâ–væ÷&–ærF†R‡GGöæÇ’GG&–'WFRà¢–b†g&öÕ67&—Bbb‡GGöæÇ”GG&–'WFU7V6–f–VB¢&WGW&âfÇ6S° ¢òò6V7W&R6öö¶–W2&R66WFVBöæÇ’g&öÒ6V7W&R÷&–v–ââ÷F†W'v—6Rà¢òò7F—fR…EEGF6¶W"6÷VÆBÆçB7FFRF†B—2ÆFW"6VçB÷fW"…EE2à¢–b‡6V7W&Rbb—56V7W&U&WVW7B¢&WGW&âfÇ6S° ¢–b‡6ÖU6—FRÓÒ6öö¶–U6ÖU6—FRäæöæRbb6V7W&R¢&WGW&âfÇ6S° ¢–b‡'F—F–öæVBbb6V7W&R¢&WGW&âfÇ6S° ¢–b†æÖRå7F'G5v—F‚‚%õõ6V7W&RÒ"Â7G&–æt6ö×&—6öâä÷&F–æÂ’bb‚6V7W&RÇÂ—56V7W&U&WVW7B’¢&WGW&âfÇ6S° ¢–b†æÖRå7F'G5v—F‚‚%õô†÷7BÒ"Â7G&–æt6ö×&—6öâä÷&F–æÂ’b`¢‚6V7W&RÇÀ¢—56V7W&U&WVW7BÇÀ¢FöÖ–äGG&–'WFU7V6–f–VBÇÀ¢F„GG&–'WFU7V6–f–VBÇÀ¢7G&–æräWVÇ2‡F‚Â"ò"Â7G&–æt6ö×&—6öâä÷&F–æÂ’’¢°¢&WGW&âfÇ6S°¢Ğ ¢7GVÅ'F—F–öä¶W’Ò'F—F–öæVBò'F—F–öä¶W’¢çVÆÃ°¢6öö¶–RÒæWr6öö¶–P¢°¢æÖRÒæÖRÀ¢fÇVRÒfÇVRÀ¢FöÖ–âÒFöÖ–âÀ¢F‚Ò7G&–ærä—4çVÆÄ÷%v†—FU76R‡F‚’ò"ò"¢F‚À¢†÷7DöæÇ’ÒFöÖ–äGG&–'WFU7V6–f–VBÀ¢6V7W&RÒ6V7W&RÀ¢‡GGöæÇ’Ò‡GGöæÇ’À¢6ÖU6—FRÒ6ÖU6—FRÀ¢òòÖ‚ÔvRF¶W2&V6VFVæ6R÷fW"W‡—&W2&Vv&FÆW72öbGG&–'WFR÷&FW"à¢W‡—&W2ÒÖ„vTW‡—'’óòW‡—&W4GG&–'WFRÀ¢'F—F–öä¶W’Ò7GVÅ'F—F–öä¶W¢Ó° ¢&WGW&âG'VS°¢Ğ ¢&—fFR7FF–2FFUF–ÖTöfg6WB6ö×WFTÖ„vTW‡—'’†ÆöærÖ„vR¢°¢–b†Ö„vRÃÒ¢&WGW&âFFUF–ÖTöfg6WBäÖ–åfÇVS° ¢f"æ÷rÒFFUF–ÖTöfg6WBåWF4æ÷s°¢f"Ö…6V6öæG2Ò„FFUF–ÖTöfg6WBäÖ…fÇVRÒæ÷r’åF÷FÅ6V6öæG3°¢–b†Ö„vRãÒÖ…6V6öæG2¢&WGW&âFFUF–ÖTöfg6WBäÖ…fÇVS° ¢&WGW&âæ÷räFE6V6öæG2†Ö„vR“°¢Ğ ¢&—fFR7FF–2&ööÂ—46öö¶–TæÖR‡7G&–ærfÇVR¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡fÇVR’’&WGW&âfÇ6S° ¢f÷&V6‚‡f"2–âfÇVR¢°¢–b†2âƒtb’&WGW&âfÇ6S°¢–b†6†"ä—4ÆWGFW$÷$F–v—B†2’’6öçF–çVS°¢–b†2—2rr÷"r2r÷"rBr÷"rRr÷"rbr÷"uÂrr÷"r¢r÷"r²r÷"rÒr÷"râr÷"uâr÷"uòr÷"vr÷"wÂr÷"wâr¢6öçF–çVS°¢&WGW&âfÇ6S°¢Ğ ¢&WGW&âG'VS°¢Ğ ¢&—fFR7FF–2&ööÂ—56fT6öö¶–UfÇVR‡7G&–ærfÇVR¢°¢–b‡fÇVRÓÒçVÆÂ’&WGW&âfÇ6S°¢f÷&V6‚‡f"2–âfÇVR¢°¢–b†6†"ä—46öçG&öÂ†2’ÇÂ2ÓÒs²r’&WGW&âfÇ6S°¢Ğ¢&WGW&âG'VS°¢Ğ ¢&—fFR7FF–2&ööÂ—46öö¶–TGG&–'WFU6fR‡7G&–æræÖRÂ7G&–ærfÇVR¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’†æÖR’’&WGW&âfÇ6S°¢f÷&V6‚‡f"2–âæÖR¢°¢–b†6†"ä—46öçG&öÂ†2’ÇÂ2ÓÒs²r’&WGW&âfÇ6S°¢Ğ¢–b‡fÇVRÓÒçVÆÂ’&WGW&âG'VS°¢f÷&V6‚‡f"2–âfÇVR¢°¢–b†6†"ä—46öçG&öÂ†2’ÇÂ2ÓÒs²r’&WGW&âfÇ6S°¢Ğ¢&WGW&âG'VS°¢Ğ ¢&—fFR7FF–2&ööÂG'”6æöæ–6Æ—¦T6öö¶–TFöÖ–â‡7G&–ærfÇVRÂ÷WB7G&–ær6æöæ–6ÄFöÖ–â¢°¢6æöæ–6ÄFöÖ–âÒ7G&–æräV×G“°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R‡fÇVR’¢&WGW&âfÇ6S° ¢f"6æF–FFRÒ6—FT–FVçF—G•6W'f–6RäFVfVÇBä6æöæ–6Æ—¦T†÷7B€¢fÇVRåG&–Ò‚’åG&–Õ7F'B‚râr’“°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’†6æF–FFR’¢&WGW&âfÇ6S°¢–b‚—466–”6öö¶–TFöÖ–â†6æF–FFR’¢&WGW&âfÇ6S° ¢6æöæ–6ÄFöÖ–âÒ6æF–FFS°¢&WGW&âG'VS°¢Ğ ¢&—fFR7FF–2&ööÂ—466–”6öö¶–TFöÖ–â‡7G&–ærfÇVR¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡fÇVR’ÇÂfÇVRäÆVæwF‚â#S2¢&WGW&âfÇ6S° ¢òò6öö¶–RFöÖ–â—2â44”’Då2æÖR†W&Râ•Æ—FW&Ç2&R66WFVBöæÇ¢òòf÷"W†7BÖ†÷7BÖF6†–ær'’FöÖ–äÖF6†W3²æò7Vff—‚ÖF6†–ær—2ÆÆ÷vV@¢òò'’F†R7F÷&vRÆ–W"f÷"†÷7BÖöæÇ’÷&–v–âà¢–b…7—7FVÒäæWBä•FG&W72åG'•'6R‡fÇVRÂ÷WBò’¢&WGW&âG'VS° ¢f"Æ&VÇ2ÒfÇVRå7Æ—B‚rârÂ7G&–æu7Æ—D÷F–öç2äæöæR“°¢f÷&V6‚‡f"Æ&VÂ–âÆ&VÇ2¢°¢–b†Æ&VÂäÆVæwF‚ÓÒÇÂÆ&VÂäÆVæwF‚âc2’&WGW&âfÇ6S°¢–b‚6†"ä—4ÆWGFW$÷$F–v—B†Æ&VÅ³Ò’ÇÂ6†"ä—4ÆWGFW$÷$F–v—B†Æ&VÅµãÒ’’&WGW&âfÇ6S°¢f÷&V6‚‡f"2–âÆ&VÂ¢°¢–b†2âƒtbÇÂ‚6†"ä—4ÆWGFW$÷$F–v—B†2’bb2ÒrÒr’’&WGW&âfÇ6S°¢Ğ¢Ğ ¢&WGW&âG'VS°¢Ğ ¢&—fFR7FF–26öö¶–U6ÖU6—FR'6U6ÖU6—FR‡7G&–ærfÇVR¢°¢&WGW&â‡fÇVRóò7G&–æräV×G’’åG&–Ò‚’åFôÆ÷vW$–çf&–çB‚’7v—F6€¢°¢'7G&–7B"Óâ6öö¶–U6ÖU6—FRå7G&–7BÀ¢&æöæR"Óâ6öö¶–U6ÖU6—FRäæöæRÀ¢&Æ‚"Óâ6öö¶–U6ÖU6—FRäÆ‚À¢òÓâ6öö¶–U6ÖU6—FRåVç7V6–f–V@¢Ó°¢Ğ ¢&—fFR7FF–27G&–ærFõ6ÖU6—FU7G&–ær„6öö¶–U6ÖU6—FR6ÖU6—FR¢°¢&WGW&â6ÖU6—FR7v—F6€¢°¢6öö¶–U6ÖU6—FRå7G&–7BÓâ'7G&–7B"À¢6öö¶–U6ÖU6—FRäæöæRÓâ&æöæR"À¢6öö¶–U6ÖU6—FRåVç7V6–f–VBÓâ&FVfVÇB"À¢òÓâ&FVfVÇB ¢Ó°¢Ğ ¢&—fFR7FF–27G&–ærvWDFVfVÇD6öö¶–UF‚‡7G&–ær&WVW7EF‚¢°¢–b‡7G&–ærä—4çVÆÄ÷$V×G’‡&WVW7EF‚’ÇÂ&WVW7EF‚ÓÒ"ò"¢&WGW&â"ò#° ¢f"–æFW‚Ò&WVW7EF‚äÆ7D–æFW„öb‚ròr“°¢&WGW&â–æFW‚ÃÒò"ò"¢&WVW7EF…²âæ–æFW…Ó°¢Ğ ¢&—fFR7FF–2&ööÂFöÖ–äÖF6†W2‡7G&–ær†÷7BÂ7G&–ær6öö¶–TFöÖ–â¢°¢–b‡7G&–ærä—4çVÆÄ÷%v†—FU76R††÷7B’ÇÂ7G&–ærä—4çVÆÄ÷%v†—FU76R†6öö¶–TFöÖ–â’¢&WGW&âfÇ6S° ¢–b††÷7BäWVÇ2†6öö¶–TFöÖ–âÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R’¢&WGW&âG'VS° ¢òò•Æ—FW&Ç2Fòæ÷B†fRDå27V&FöÖ–ç2f÷"6öö¶–RÖFöÖ–âÖF6†–ærà¢–b…7—7FVÒäæWBä•FG&W72åG'•'6R††÷7BÂ÷WBò’ÇÀ¢7—7FVÒäæWBä•FG&W72åG'•'6R†6öö¶–TFöÖ–âÂ÷WBò’¢°¢&WGW&âfÇ6S°¢Ğ ¢&WGW&â†÷7BäVæG5v—F‚‚"â"²6öö¶–TFöÖ–âÂ7G&–æt6ö×&—6öâä÷&F–æÄ–væ÷&T66R“°¢Ğ ¢&—fFR7FF–26öö¶–T6öçFW‡B'V–ÆD6öçFW‡B…W&’&WVW7EW&’ÂW&’F÷ÆWfVÄFö7VÖVçEW&’¢°¢f"VffV7F—fUF÷ÆWfVÂÒF÷ÆWfVÄFö7VÖVçEW&’óò&WVW7EW&“°¢f"'F—F–öä¶W’Ò7F÷&vU'F—F–öä¶W”f7F÷'’ä6ö×WFR€¢VffV7F—fUF÷ÆWfVÃòä'6öÇWFUW&’À¢&WVW7EW&“òä'6öÇWFUW&’“° ¢&WGW&âæWr6öö¶–T6öçFW‡B‡'F—F–öä¶W’“°¢Ğ ¢&—fFR&VFöæÇ’7G'V7B6öö¶–T6öçFW‡@¢°¢V&Æ–26öö¶–T6öçFW‡B…7F÷&vU'F—F–öä¶W’'F—F–öä¶W’¢°¢'F—F–öä¶W’Ò'F—F–öä¶W“°¢Ğ ¢V&Æ–27F÷&vU'F—F–öä¶W’'F—F–öä¶W’²vWC²Ğ¢V&Æ–2&ööÂ—5F†—&E'G’Óâ'F—F–öä¶W’ä—5F†—&E'G“°¢Ğ¢Ğ§Ğ 