using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Security;
using FenBrowser.Core.Security.Corb;
using FenBrowser.Core.Storage;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Globalization;
using System.Buffers;

namespace FenBrowser.Core
{
    public enum FetchStatus
    {
        Success,
        ConnectionFailed,
        SslError,
        Timeout,
        NotFound,
        LimitExceeded,
        UnknownError
    }

    public enum FetchFailureReasonCode
    {
        None,
        LimitExceeded,
        RedirectLimitExceeded,
        Timeout,
        MalformedInput,
        TransportFailure,
        HttpError,
        CorbBlocked,
        XFrameBlocked,
        BodyReadFailed,
        Unknown
    }

    /// <summary>
    /// Parsed value of the X-Frame-Options response header.
    /// Controls whether the fetched document may be displayed in a frame.
    /// </summary>
    public enum XFrameOptionsPolicy
    {
        /// <summary>No X-Frame-Options header present — framing is allowed.</summary>
        None,
        /// <summary>DENY — the document must not be displayed in any frame.</summary>
        Deny,
        /// <summary>SAMEORIGIN — only same-origin pages may frame this document.</summary>
        SameOrigin,
        /// <summary>ALLOW-FROM (deprecated) — only the specified origin may frame this document.</summary>
        AllowFrom,
    }

    public enum ReferrerPolicyDirective
    {
        StrictOriginWhenCrossOrigin,
        NoReferrer,
        NoReferrerWhenDowngrade,
        SameOrigin,
        Origin,
        StrictOrigin,
        OriginWhenCrossOrigin,
        UnsafeUrl,
    }

    public class FetchResult
    {
        public FetchStatus Status;
        public string Content;
        public string ErrorDetail;
        public int StatusCode;
        public Uri FinalUri;
        public string ContentType;
        public CertificateInfo Certificate;
        public System.Net.Security.SslPolicyErrors SslErrors;
        public HttpResponseHeaders Headers;
        public IReadOnlyDictionary<string, string[]> HeaderSnapshot;
        public bool Redirected;
        public int RedirectCount;
        public IReadOnlyList<string> RedirectChain;
        public FetchFailureReasonCode FailureReason { get; set; } = FetchFailureReasonCode.None;
        public string LimitType { get; set; }
        public long? DurationMs { get; set; }
        public int? InputSizeBytes { get; set; }
        public bool IsRetryable { get; set; }

        /// <summary>Parsed X-Frame-Options policy from the response headers.</summary>
        public XFrameOptionsPolicy XFrameOptions { get; set; } = XFrameOptionsPolicy.None;
        /// <summary>
        /// For ALLOW-FROM policy, the allowed origin URI string.
        /// Null for DENY / SAMEORIGIN / None.
        /// </summary>
        public string XFrameAllowFromUri { get; set; }
        public ReferrerPolicyDirective ReferrerPolicy { get; set; } = ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;

        /// <summary>Parsed Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy from the response headers.</summary>
        public CrossOriginIsolationPolicy CrossOriginIsolation { get; set; } = new();
        /// <summary>Parsed Cross-Origin-Resource-Policy header value from the response headers.</summary>
        public string CrossOriginResourcePolicy { get; set; }

        /// <summary>Parsed Permissions-Policy header value from the response headers.</summary>
        public Security.PermissionsPolicy PermissionsPolicy { get; set; } = Security.PermissionsPolicy.None;

        public bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            if (HeaderSnapshot != null && HeaderSnapshot.TryGetValue(name, out var snapshotValues))
            {
                values = snapshotValues;
                return true;
            }

            if (Headers != null && Headers.TryGetValues(name, out var liveValues))
            {
                values = liveValues;
                return true;
            }

            values = Array.Empty<string>();
            return false;
        }
    }

    public sealed class ResourceManager
    {
        public static System.Action<string> LogSink;

        // Phase 2.3: Sharded Caches
        public sealed class TextEntry
        {
            public string Body;
            public string ContentType;
            public Uri FinalUri;
            public int StatusCode;
            public IReadOnlyDictionary<string, string[]> HeaderSnapshot;
            public bool Redirected;
            public int RedirectCount;
            public IReadOnlyList<string> RedirectChain;
            public XFrameOptionsPolicy XFrameOptions;
            public string XFrameAllowFromUri;
            public ReferrerPolicyDirective ReferrerPolicy;
            public CrossOriginIsolationPolicy CrossOriginIsolation;
            public string CrossOriginResourcePolicy;
            public Security.PermissionsPolicy PermissionsPolicy;
            public DateTimeOffset ExpiresAtUtc;
        }
        private readonly FenBrowser.Core.Cache.ShardedCache<TextEntry> _textCache = new FenBrowser.Core.Cache.ShardedCache<TextEntry>(128);

        public sealed class ImgEntry { public byte[] Buffer; public string ContentType; }
        private readonly FenBrowser.Core.Cache.ShardedCache<ImgEntry> _imgCache = new FenBrowser.Core.Cache.ShardedCache<ImgEntry>(64);
public Uri LastTextResponseUri { get; private set; }
        public ReferrerPolicyDirective ActiveReferrerPolicy { get; private set; } = ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;

        /// <summary>
        /// Parses a Referrer-Policy header value into a ReferrerPolicyDirective.
        /// </summary>
        public static ReferrerPolicyDirective ParseReferrerPolicy(string headerValue)
        {
            ReferrerPolicyDirective? parsed = null;
            if (!string.IsNullOrWhiteSpace(headerValue))
            {
                foreach (var policyGroup in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var rawToken in policyGroup.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (TryParseReferrerPolicyToken(rawToken, out var candidate))
                        {
                            // Referrer-Policy uses the last recognized token from the
                            // policy list, so keep scanning instead of returning early.
                            parsed = candidate;
                        }
                    }
                }
            }

            return parsed ?? ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;
        }

        private static bool TryParseReferrerPolicyToken(
            string rawToken,
            out ReferrerPolicyDirective directive)
        {
            switch ((rawToken ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "no-referrer":
                    directive = ReferrerPolicyDirective.NoReferrer;
                    return true;
                case "no-referrer-when-downgrade":
                    directive = ReferrerPolicyDirective.NoReferrerWhenDowngrade;
                    return true;
                case "same-origin":
                    directive = ReferrerPolicyDirective.SameOrigin;
                    return true;
                case "origin":
                    directive = ReferrerPolicyDirective.Origin;
                    return true;
                case "strict-origin":
                    directive = ReferrerPolicyDirective.StrictOrigin;
                    return true;
                case "origin-when-cross-origin":
                    directive = ReferrerPolicyDirective.OriginWhenCrossOrigin;
                    return true;
                case "unsafe-url":
                    directive = ReferrerPolicyDirective.UnsafeUrl;
                    return true;
                case "strict-origin-when-cross-origin":
                    directive = ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;
                    return true;
                default:
                    directive = default;
                    return false;
            }
        }


        private readonly string _cacheRoot;
        private readonly INetworkClient _client;

        private int _blockedRequestCount;
        public int BlockedRequestCount => System.Threading.Volatile.Read(ref _blockedRequestCount);
        public event EventHandler<int> BlockedCountChanged;
        
        // DevTools Network Monitoring Events
        public event Action<string, HttpRequestMessage> NetworkRequestStarting;
        public event Action<string, HttpResponseMessage> NetworkRequestCompleted;
        public event Action<string, Exception> NetworkRequestFailed;

        public void ResetBlockedCount()
        {
            System.Threading.Interlocked.Exchange(ref _blockedRequestCount, 0);
            PublishBlockedCount(0);
        }

        private void IncrementBlockedRequestCount()
        {
            var count = System.Threading.Interlocked.Increment(ref _blockedRequestCount);
            PublishBlockedCount(count);
        }

        private void PublishBlockedCount(int count)
        {
            try
            {
                BlockedCountChanged?.Invoke(this, count);
            }
            catch (Exception ex)
            {
                // A diagnostics/UI subscriber must never turn a blocked network request
                // into a transport failure or corrupt the counter state.
                EngineLogCompat.Debug($"[Network] Blocked-count subscriber failed: {ex.Message}", LogCategory.Network);
            }
        }

        private readonly bool _isPrivate;
        private static int _policyBindingDiagnosticsLogged;
        private static long _nextRequestId;
        private static readonly CorbFilter SharedCorbFilter = new();

        public CspPolicy ActivePolicy { get; set; }
        public BrowserCookieJar CookieJar { get; }

        public ResourceManager(
            HttpClient http,
            bool isPrivate = false,
            BrowserCookieJar cookieJar = null)
        {
            _isPrivate = isPrivate;
            CookieJar = cookieJar ?? new BrowserCookieJar();
            if (!_isPrivate)
            {
                _cacheRoot = Path.Combine(AppContext.BaseDirectory, "Cache");
                Directory.CreateDirectory(_cacheRoot);
            }
            else
            {
                // In private mode, use a temp path or just don't use disk at all. 
                // We'll set it to null and check before access.
                _cacheRoot = null; 
            }

            var handlers = new List<INetworkHandler>
            {
                new TrackingPreventionHandler(), // Enhanced Tracking Prevention (Phase 5)
                new AdBlockHandler(() => BrowserSettings.Instance.EnableTrackingPrevention),
                new PrivacyHandler(),
                new SafeBrowsingHandler(() => BrowserSettings.Instance.SafeBrowsing),
                // Only enable HSTS disk persistence if not private
                new HstsHandler(_isPrivate ? null : Path.Combine(AppContext.BaseDirectory, "Cache")), 
                new HttpHandler(http)
            };
            _client = new NetworkClient(handlers);
            EmitPolicyBindingDiagnostics();
        }

        private static void EmitPolicyBindingDiagnostics()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _policyBindingDiagnosticsLogged, 1, 0) != 0)
            {
                return;
            }

            var settings = BrowserSettings.Instance;
            var enforced = string.Join(", ", new[]
            {
                $"SendDoNotTrack={settings.SendDoNotTrack}",
                $"BlockThirdPartyCookies={settings.BlockThirdPartyCookies}",
                $"EnableTrackingPrevention={settings.EnableTrackingPrevention}",
                $"UseSecureDNS={settings.UseSecureDNS}",
                $"SafeBrowsing={settings.SafeBrowsing}",
                $"ImproveBrowser={settings.ImproveBrowser}",
                $"BlockPopups={settings.BlockPopups}",
                $"AllowFileSchemeNavigation={settings.AllowFileSchemeNavigation}",
                $"AllowAutomationFileNavigation={settings.AllowAutomationFileNavigation}"
            });
            var pending = Array.Empty<string>();

            EngineLogCompat.Info($"[PolicyBindings] Runtime-enforced toggles: {enforced}", LogCategory.Network);
            if (pending.Length == 0)
            {
                EngineLogCompat.Info("[PolicyBindings] UI toggles pending full runtime wiring: none", LogCategory.Network);
                return;
            }

            EngineLogCompat.Warn($"[PolicyBindings] UI toggles pending full runtime wiring: {string.Join(", ", pending)}", LogCategory.Network);
        }

        public void ClearCache()
        {
            // Clear memory
            _textCache.Clear();
            _imgCache.Clear();

            // Clear disk (skip if private or path null)
            try
            {
                if (!_isPrivate && !string.IsNullOrEmpty(_cacheRoot) && Directory.Exists(_cacheRoot))
                {
                    Directory.Delete(_cacheRoot, true);
                    Directory.CreateDirectory(_cacheRoot);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClearCache] Failed to delete disk cache: {ex.Message}");
            }
        }



        private static bool LooksTextual(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            var ct = contentType.ToLowerInvariant();
            return ct.StartsWith("text/") || ct.Contains("javascript") || ct.Contains("json");
        }

        private static IReadOnlyDictionary<string, string[]> SnapshotHeaders(HttpResponseMessage response)
        {
            var snapshot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (response?.Headers != null)
            {
                foreach (var header in response.Headers)
                {
                    snapshot[header.Key] = header.Value.ToArray();
                }
            }

            if (response?.Content?.Headers != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    snapshot[header.Key] = header.Value.ToArray();
                }
            }

            return snapshot;
        }

        private static bool TryGetTextCacheExpiry(HttpResponseMessage response, out DateTimeOffset expiresAtUtc)
        {
            expiresAtUtc = default;
            if (response == null || !response.IsSuccessStatusCode)
            {
                return false;
            }

            var cacheControl = response.Headers.CacheControl;
            if (cacheControl?.NoStore == true || cacheControl?.NoCache == true || cacheControl?.Private == true)
            {
                return false;
            }

            if (response.Headers.TryGetValues("Set-Cookie", out _))
            {
                return false;
            }

            if (response.Headers.Vary != null && response.Headers.Vary.Count > 0)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (cacheControl?.MaxAge is TimeSpan maxAge && maxAge > TimeSpan.Zero)
            {
                expiresAtUtc = now + maxAge;
                return true;
            }

            var expires = response.Content?.Headers?.Expires;
            if (expires.HasValue && expires.Value > now)
            {
                expiresAtUtc = expires.Value;
                return true;
            }

            return false;
        }

        private string BuildTextCacheKey(
            FetchContext context,
            string accept,
            Uri topLevelDocumentUri,
            string secFetchDest,
            string fetchMode)
        {
            var requestUri = context?.RequestUri;
            var credentials = context?.CredentialsMode ?? string.Empty;
            var cookieIdentity = string.Empty;
            if (requestUri != null && AreCredentialsAllowed(context, requestUri) && CookieJar != null)
            {
                var cookieHeader = CookieJar.GetRequestCookieHeader(
                    requestUri,
                    topLevelDocumentUri,
                    IsTopLevelDocumentRequest(secFetchDest),
                    context?.Method ?? HttpMethod.Get.Method);
                if (!string.IsNullOrEmpty(cookieHeader))
                {
                    cookieIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cookieHeader)));
                }
            }

            var initiator = ExtractOrigin(context?.InitiatorUri)?.AbsoluteUri ?? string.Empty;
            return string.Join("\n", new[]
            {
                requestUri?.AbsoluteUri ?? string.Empty,
                fetchMode ?? string.Empty,
                secFetchDest ?? string.Empty,
                credentials,
                accept ?? string.Empty,
                initiator,
                cookieIdentity
            });
        }

        private static void AddHeaderSafe(HttpRequestMessage req, string name, string value)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    req.Headers.TryAddWithoutValidation(name, value);
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[Network] Failed to add header '{name}': {ex.Message}", LogCategory.Network);
            }
        }

        private static ResilienceSettings GetResilienceSettings()
        {
            var resilience = BrowserSettings.Instance?.Resilience;
            if (resilience == null)
            {
                resilience = new ResilienceSettings();
                resilience.Normalize();
            }

            return resilience;
        }

        private static int ResolveTimeoutSeconds(string secFetchDest)
        {
            var resilience = GetResilienceSettings();
            var normalizedDest = (secFetchDest ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedDest == "document" || normalizedDest == "iframe")
            {
                return Math.Max(1, resilience.NavigationTimeoutSeconds);
            }

            return Math.Max(1, resilience.RequestTimeoutSeconds);
        }

        private static Uri ExtractOrigin(Uri candidate)
        {
            if (candidate == null || !candidate.IsAbsoluteUri)
            {
                return null;
            }

            if (string.Equals(candidate.Scheme, "data", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Scheme, "blob", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var port = candidate.IsDefaultPort ? -1 : candidate.Port;
                var builder = new UriBuilder(candidate.Scheme, candidate.Host, port);
                return builder.Uri;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            if (left == null || right == null || !left.IsAbsoluteUri || !right.IsAbsoluteUri)
            {
                return false;
            }

            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        private static bool IsDowngrade(Uri referer, Uri request)
        {
            return referer != null &&
                   request != null &&
                   string.Equals(referer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(request.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAutomationContext()
        {
            var webdriverEnabled = string.Equals(
                Environment.GetEnvironmentVariable("FEN_WEBDRIVER"),
                "1",
                StringComparison.Ordinal);
            var automationMode = string.Equals(
                Environment.GetEnvironmentVariable("FEN_AUTOMATION_MODE"),
                "1",
                StringComparison.Ordinal);
            return webdriverEnabled || automationMode;
        }

        private static bool IsFileSchemeAccessAllowed()
        {
            var settings = BrowserSettings.Instance;
            if (!settings.AllowFileSchemeNavigation)
            {
                return false;
            }

            if (!IsAutomationContext())
            {
                return true;
            }

            if (settings.AllowAutomationFileNavigation)
            {
                return true;
            }

            return string.Equals(
                Environment.GetEnvironmentVariable("FEN_ALLOW_AUTOMATION_FILE_NAVIGATION"),
                "1",
                StringComparison.Ordinal);
        }

        private static bool IsSupportedFetchScheme(Uri url)
        {
            if (url == null || !url.IsAbsoluteUri)
            {
                return false;
            }

            var scheme = url.Scheme;
            return string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, "data", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, "about", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(scheme, "fen", StringComparison.OrdinalIgnoreCase);
        }

        private static ReferrerPolicyDirective ParseReferrerPolicy(HttpResponseMessage response)
        {
            if (response?.Headers == null || !response.Headers.TryGetValues("Referrer-Policy", out var values))
            {
                return ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;
            }

            return ParseReferrerPolicy(string.Join(",", values));
        }

        private static void ParseXFrameOptions(
            HttpResponseMessage response,
            out XFrameOptionsPolicy policy,
            out string allowFromUri)
        {
            policy = XFrameOptionsPolicy.None;
            allowFromUri = null;

            if (response?.Headers == null ||
                !response.Headers.TryGetValues("X-Frame-Options", out var values))
            {
                return;
            }

            XFrameOptionsPolicy? parsedPolicy = null;
            string parsedAllowFrom = null;

            foreach (var rawHeader in values)
            {
                if (string.IsNullOrWhiteSpace(rawHeader))
                    continue;

                foreach (var rawDirective in rawHeader.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var directive = rawDirective.Trim();
                    XFrameOptionsPolicy candidate;
                    string candidateAllowFrom = null;

                    if (string.Equals(directive, "DENY", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = XFrameOptionsPolicy.Deny;
                    }
                    else if (string.Equals(directive, "SAMEORIGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        candidate = XFrameOptionsPolicy.SameOrigin;
                    }
                    else
                    {
                        const string allowFrom = "ALLOW-FROM";
                        if (directive.Length <= allowFrom.Length ||
                            !directive.StartsWith(allowFrom, StringComparison.OrdinalIgnoreCase) ||
                            (directive[allowFrom.Length] != ' ' && directive[allowFrom.Length] != '\t'))
                        {
                            // Invalid/unknown directives are ignored rather than substring-matched.
                            continue;
                        }

                        var candidateText = directive.Substring(allowFrom.Length).Trim();
                        if (!Uri.TryCreate(candidateText, UriKind.Absolute, out var candidateUri))
                        {
                            continue;
                        }

                        candidate = XFrameOptionsPolicy.AllowFrom;
                        candidateAllowFrom = candidateUri.AbsoluteUri;
                    }

                    if (parsedPolicy.HasValue &&
                        (parsedPolicy.Value != candidate ||
                         !string.Equals(parsedAllowFrom, candidateAllowFrom, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Conflicting valid XFO policies are ambiguous. Fail closed for framing.
                        policy = XFrameOptionsPolicy.Deny;
                        allowFromUri = null;
                        return;
                    }

                    parsedPolicy = candidate;
                    parsedAllowFrom = candidateAllowFrom;
                }
            }

            policy = parsedPolicy ?? XFrameOptionsPolicy.None;
            allowFromUri = parsedAllowFrom;
        }

        private static bool IsFrameEmbeddingAllowed(
            XFrameOptionsPolicy policy,
            string allowFromUri,
            Uri embeddingDocumentUri,
            Uri framedDocumentUri)
        {
            switch (policy)
            {
                case XFrameOptionsPolicy.None:
                    return true;
                case XFrameOptionsPolicy.Deny:
                    return false;
                case XFrameOptionsPolicy.SameOrigin:
                    return IsSameOrigin(embeddingDocumentUri, framedDocumentUri);
                case XFrameOptionsPolicy.AllowFrom:
                    if (string.IsNullOrWhiteSpace(allowFromUri) || embeddingDocumentUri == null)
                    {
                        return false;
                    }

                    if (!Uri.TryCreate(allowFromUri, UriKind.Absolute, out var allowedUri))
                    {
                        return false;
                    }

                    return IsSameOrigin(embeddingDocumentUri, allowedUri);
                default:
                    return true;
            }
        }

        private static Uri ComputeReferrerHeader(Uri candidate, Uri requestUri, ReferrerPolicyDirective policy)
        {
            if (candidate == null || requestUri == null || !candidate.IsAbsoluteUri || !requestUri.IsAbsoluteUri)
            {
                return null;
            }

            var sameOrigin = IsSameOrigin(candidate, requestUri);
            var downgrade = IsDowngrade(candidate, requestUri);
            var originOnly = ExtractOrigin(candidate);

            switch (policy)
            {
                case ReferrerPolicyDirective.NoReferrer:
                    return null;
                case ReferrerPolicyDirective.NoReferrerWhenDowngrade:
                    return downgrade ? null : candidate;
                case ReferrerPolicyDirective.SameOrigin:
                    return sameOrigin ? candidate : null;
                case ReferrerPolicyDirective.Origin:
                    return originOnly;
                case ReferrerPolicyDirective.StrictOrigin:
                    return downgrade ? null : originOnly;
                case ReferrerPolicyDirective.OriginWhenCrossOrigin:
                    return sameOrigin ? candidate : originOnly;
                case ReferrerPolicyDirective.UnsafeUrl:
                    return candidate;
                case ReferrerPolicyDirective.StrictOriginWhenCrossOrigin:
                default:
                    if (sameOrigin) return candidate;
                    return downgrade ? null : originOnly;
            }
        }

        private static void ApplyRefererHeader(HttpRequestMessage req, Uri refererCandidate, Uri requestUri, ReferrerPolicyDirective policy)
        {
            var computed = ComputeReferrerHeader(refererCandidate, requestUri, policy);
            if (computed == null)
            {
                return;
            }

            try
            {
                req.Headers.Referrer = computed;
            }
            catch
            {
                AddHeaderSafe(req, "Referer", computed.AbsoluteUri);
            }
        }

        private void AttachCookies(HttpRequestMessage request, Uri topLevelDocumentUri, string secFetchDest)
        {
            if (request?.RequestUri == null || CookieJar == null || request.Headers.Contains("Cookie"))
            {
                return;
            }

            bool isTopLevelNavigation = IsTopLevelDocumentRequest(secFetchDest);
            var cookieHeader = CookieJar.GetRequestCookieHeader(
                request.RequestUri,
                topLevelDocumentUri,
                isTopLevelNavigation,
                request.Method?.Method ?? HttpMethod.Get.Method);

            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        private void StoreResponseCookies(HttpResponseMessage response, Uri topLevelDocumentUri)
        {
            CookieJar?.StoreResponseCookies(
                response,
                topLevelDocumentUri,
                BrowserSettings.Instance.BlockThirdPartyCookies);
        }

        private static string GetHeaderValue(HttpRequestHeaders headers, string name)
        {
            if (headers != null && headers.TryGetValues(name, out var values))
            {
                return values.FirstOrDefault();
            }

            return null;
        }

        private static bool IsNavigationDestination(string secFetchDest)
        {
            var normalized = (secFetchDest ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "document" || normalized == "iframe";
        }

        private static string DetermineFetchMode(string secFetchDest)
        {
            var normalized = (secFetchDest ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "document" || normalized == "iframe")
            {
                return "navigate";
            }

            if (normalized == "style" ||
                normalized == "script" ||
                normalized == "image" ||
                normalized == "font" ||
                normalized == "audio" ||
                normalized == "video" ||
                normalized == "track" ||
                normalized == "object" ||
                normalized == "embed")
            {
                return "no-cors";
            }

            return "cors";
        }

        private static bool IsTopLevelDocumentRequest(string secFetchDest)
        {
            var normalized = (secFetchDest ?? string.Empty).Trim().ToLowerInvariant();
            return normalized == "document";
        }

        private static void ApplyNavigationRequestHeaders(
            HttpRequestMessage req,
            string secFetchDest,
            bool isUserInitiatedNavigation = true)
        {
            if (req == null || !IsTopLevelDocumentRequest(secFetchDest))
            {
                return;
            }

            if (isUserInitiatedNavigation)
            {
                AddHeaderSafe(req, "Sec-Fetch-User", "?1");
            }

            AddHeaderSafe(req, "Upgrade-Insecure-Requests", "1");
        }

        private static bool ShouldApplyCorb(string fetchMode, string secFetchDest)
        {
            if (!string.Equals(fetchMode, "no-cors", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = (secFetchDest ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            return normalized != "document" && normalized != "iframe";
        }

        private bool ShouldBlockCorb(
            string fetchMode,
            string secFetchDest,
            Uri requestOriginCandidate,
            Uri responseUri,
            HttpResponseMessage response,
            ReadOnlySpan<byte> responseBodyPrefix,
            out string blockReason)
        {
            blockReason = null;
            if (!ShouldApplyCorb(fetchMode, secFetchDest))
            {
                return false;
            }

            var requestOrigin = ExtractOrigin(requestOriginCandidate);
            if (requestOrigin == null || responseUri == null)
            {
                return false;
            }

            string contentType = response?.Content?.Headers?.ContentType?.ToString();
            string contentTypeOptions = null;
            if (response?.Headers != null &&
                response.Headers.TryGetValues("X-Content-Type-Options", out var xctoValues))
            {
                contentTypeOptions = string.Join(",", xctoValues);
            }

            var corbResult = SharedCorbFilter.Evaluate(
                fetchMode,
                requestOrigin.AbsoluteUri,
                responseUri.AbsoluteUri,
                contentType,
                contentTypeOptions,
                responseBodyPrefix);

            if (corbResult.Verdict != CorbVerdict.Block)
            {
                return false;
            }

            IncrementBlockedRequestCount();
            blockReason = corbResult.Reason;
            EngineLogCompat.Warn(
                $"[CORB] Blocked cross-origin {secFetchDest} response '{responseUri}' for origin '{requestOrigin}'. {corbResult.Reason}",
                LogCategory.Network);
            return true;
        }

        private static string DecodeTextResponse(byte[] buffer, string contentTypeHeader)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return string.Empty;
            }

            string decoded;
            try
            {
                decoded = EncodingSniffer.DecodeToUtf8(buffer, contentTypeHeader);
            }
            catch
            {
                decoded = Encoding.UTF8.GetString(buffer);
            }

            return decoded;
        }

        private void AdoptResponseReferrerPolicy(HttpResponseMessage response, FetchContext context)
        {
            if (context?.IsTopLevelNavigation != true)
            {
                return;
            }

            ActiveReferrerPolicy = ParseReferrerPolicy(response);
        }

        private static string HashForFile(string key)
        {
            try
            {
                // Short, stable 64-bit FNV-1a hex digest
                var text = key ?? string.Empty;
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                unchecked
                {
                    ulong h = 1469598103934665603UL; // FNV-1a 64 offset basis
                    for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
                    return h.ToString("x16");
                }
            }
            catch { return "0"; }
        }



        // Text with redirect + small disk cache (5m TTL)
        public async Task<string> FetchTextAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            if (url == null)
                return null;

            var destination = string.IsNullOrWhiteSpace(secFetchDest) ? "empty" : secFetchDest;
            var context = new FetchContext
            {
                RequestUri = url,
                InitiatorUri = referer,
                FrameDocumentUri = referer,
                TopLevelDocumentUri = referer,
                Destination = destination,
                Mode = DetermineFetchMode(destination),
                CredentialsMode = "include",
                IsTopLevelNavigation = IsTopLevelDocumentRequest(destination),
                IsUserInitiated = false,
                Method = "GET"
            };

            var result = await FetchTextDetailedAsync(context, accept).ConfigureAwait(false);
            LastTextResponseUri = result?.FinalUri;
            if (result?.Status == FetchStatus.Success)
                return result.Content;

            if (!string.IsNullOrWhiteSpace(result?.ErrorDetail))
            {
                EngineLogCompat.Debug(
                    $"[FetchText] {url} failed: {result.ErrorDetail}",
                    LogCategory.Network);
            }

            return null;
        }

        public Task<FetchResult> FetchTextDetailedAsync(
            Uri url,
            Uri referer = null,
            string accept = null,
            string secFetchDest = null,
            bool isUserInitiatedNavigation = true)
        {
            var destination = string.IsNullOrWhiteSpace(secFetchDest) ? "empty" : secFetchDest;
            return FetchTextDetailedAsync(
                new FetchContext
                {
                    RequestUri = url,
                    InitiatorUri = referer,
                    FrameDocumentUri = referer,
                    TopLevelDocumentUri = referer,
                    Destination = destination,
                    Mode = DetermineFetchMode(destination),
                    CredentialsMode = "include",
                    IsTopLevelNavigation = IsTopLevelDocumentRequest(destination),
                    IsUserInitiated = isUserInitiatedNavigation,
                    Method = "GET"
                },
                accept);
        }

        public async Task<FetchResult> FetchTextDetailedAsync(
            FetchContext context,
            string accept = null)
        {
            if (context == null)
            {
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Fetch context is null" };
            }

            var url = context.RequestUri;
            var referer = context.InitiatorUri;
            var secFetchDest = context.Destination;
            var isUserInitiatedNavigation = context.IsUserInitiated;
            var fetchMode = string.IsNullOrWhiteSpace(context.Mode)
                ? DetermineFetchMode(secFetchDest)
                : context.Mode;
            if (url == null) return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "URL is null" };
            if (!IsSupportedFetchScheme(url))
            {
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = $"Unsupported URL scheme: {url.Scheme}",
                    FinalUri = url,
                    FailureReason = FetchFailureReasonCode.MalformedInput
                };
            }
            
            /* [PERF-REMOVED] */

            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                var maxTextBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxTextBodyBytes);
                if (!DataUrlParser.TryParse(url, maxTextBodyBytes, out var dataUrl, out var dataUrlError))
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = dataUrlError,
                        FinalUri = url,
                        FailureReason = FetchFailureReasonCode.MalformedInput,
                        IsRetryable = false
                    };
                }

                try
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.Success,
                        Content = dataUrl.DecodeText(),
                        FinalUri = url,
                        ContentType = dataUrl.ContentType,
                        InputSizeBytes = dataUrl.Bytes.Length,
                        IsRetryable = false
                    };
                }
                catch (Exception ex)
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = $"Data URL text decode failed: {ex.Message}",
                        FinalUri = url,
                        ContentType = dataUrl.ContentType,
                        FailureReason = FetchFailureReasonCode.MalformedInput,
                        IsRetryable = false
                    };
                }
            }

            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsFileSchemeAccessAllowed())
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = "Blocked by file scheme navigation policy",
                        FinalUri = url,
                        FailureReason = FetchFailureReasonCode.MalformedInput
                    };
                }

                try
                {
                    var text = await File.ReadAllTextAsync(url.LocalPath).ConfigureAwait(false);
                    return new FetchResult { Status = FetchStatus.Success, Content = text, FinalUri = url, ContentType = "text/html" };
                }
                catch (FileNotFoundException)
                {
                    return new FetchResult { Status = FetchStatus.NotFound, ErrorDetail = "File not found", FinalUri = url };
                }
                catch (Exception ex)
                {
                    return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = ex.Message, FinalUri = url };
                }
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            LastTextResponseUri = null;

            // Sharded memory lookup — populated by FetchTextAsync and by the
            // PreloadScanner-fed prefetcher. Without this, the same CSS/JS
            // URL goes over the wire every time something different in the
            // engine asks for it (FetchCssAsync, FetchTextDetailedAsync,
            // FetchTextAsync each took their own request), and the
            // speculative preload fetch was wasted because nothing else
            // consulted the cache it warmed.
            var refererOriginal = referer;
            var topLevelDocumentUri = context.TopLevelDocumentUri ??
                context.FrameDocumentUri ??
                refererOriginal ??
                url;
            var cacheKey = BuildTextCacheKey(context, accept, topLevelDocumentUri, secFetchDest, fetchMode);
            var cachePartition = context.NetworkPartitionKey.ToStorageKey();
            if (_textCache.TryGet(cachePartition, cacheKey, out var cachedDetailed))
            {
                if (cachedDetailed.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _textCache.TryRemove(cachePartition, cacheKey, out _);
                }
                else
                {
                    var cachedFinalUri = cachedDetailed.FinalUri ?? url;
                    LastTextResponseUri = cachedFinalUri;
                    return new FetchResult
                    {
                        Status = FetchStatus.Success,
                        Content = cachedDetailed.Body,
                        StatusCode = cachedDetailed.StatusCode,
                        FinalUri = cachedFinalUri,
                        ContentType = cachedDetailed.ContentType,
                        HeaderSnapshot = cachedDetailed.HeaderSnapshot,
                        Redirected = cachedDetailed.Redirected,
                        RedirectCount = cachedDetailed.RedirectCount,
                        RedirectChain = cachedDetailed.RedirectChain,
                        XFrameOptions = cachedDetailed.XFrameOptions,
                        XFrameAllowFromUri = cachedDetailed.XFrameAllowFromUri,
                        ReferrerPolicy = cachedDetailed.ReferrerPolicy,
                        CrossOriginIsolation = cachedDetailed.CrossOriginIsolation ?? new CrossOriginIsolationPolicy(),
                        CrossOriginResourcePolicy = cachedDetailed.CrossOriginResourcePolicy,
                        PermissionsPolicy = cachedDetailed.PermissionsPolicy ?? Security.PermissionsPolicy.None
                    };
                }
            }

            Uri previousRequest = null;
            var redirectChain = new List<Uri>();
            if (url != null)
            {
                redirectChain.Add(url);
            }

            try
            {
                var _startFetch = DateTimeOffset.UtcNow;
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                var maxRedirectHops = Math.Max(1, GetResilienceSettings().MaxRedirectHops);
                while (hops < maxRedirectHops)
                {
                    /* [PERF-REMOVED] */
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    var effectiveReferer = refererOriginal ?? previousRequest;
                    BrowserRequestHeaderPolicy.Apply(
                        req,
                        context,
                        context.ReferrerPolicy ?? ActiveReferrerPolicy,
                        string.IsNullOrWhiteSpace(accept)
                            ? BrowserNetworkCapabilities.DocumentAcceptHeader
                            : accept,
                        effectiveReferer);
                    var credentialsAllowed = AreCredentialsAllowed(context, current);
                    if (credentialsAllowed)
                    {
                        AttachCookies(req, topLevelDocumentUri, secFetchDest);
                    }
                    
                    var cts = new System.Threading.CancellationTokenSource();
                    try
                    {
                        int sec = ResolveTimeoutSeconds(secFetchDest);
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[Network] Failed to apply detailed-fetch timeout: {ex.Message}", LogCategory.Network);
                    }

                    try 
                    { 
                        /* [PERF-REMOVED] */
                        resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false); 
                    }
                    catch (TaskCanceledException)
                    {
                        return new FetchResult
                        {
                            Status = FetchStatus.Timeout,
                            ErrorDetail = "Connection timed out",
                            FinalUri = current,
                            Redirected = redirectChain.Count > 1,
                            RedirectCount = Math.Max(0, redirectChain.Count - 1),
                            RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                            FailureReason = FetchFailureReasonCode.Timeout,
                            IsRetryable = true
                        };
                    }
                    catch (HttpRequestException httpEx)
                    {
                        var msg = httpEx.Message;
                        if (httpEx.InnerException != null) msg += " " + httpEx.InnerException.Message;

                        // Use .NET 9 HttpRequestError enum for reliable SSL detection,
                        // then fall back to message-keyword heuristic for older inner exception types.
                        bool isSsl = httpEx.HttpRequestError == System.Net.Http.HttpRequestError.SecureConnectionError
                            || msg.Contains("SSL",      StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("cert",     StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("security", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("TLS",      StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("trust",    StringComparison.OrdinalIgnoreCase);

                        return new FetchResult
                        {
                            Status      = isSsl ? FetchStatus.SslError : FetchStatus.ConnectionFailed,
                            ErrorDetail = msg,
                            FinalUri    = current,
                            Redirected  = redirectChain.Count > 1,
                            RedirectCount = Math.Max(0, redirectChain.Count - 1),
                            RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                            FailureReason = FetchFailureReasonCode.TransportFailure,
                            IsRetryable = !isSsl
                        };
                    }
                    catch (Exception sendEx)
                    {
                         return new FetchResult
                         {
                             Status = FetchStatus.UnknownError,
                             ErrorDetail = sendEx.Message,
                             FinalUri = current,
                             Redirected = redirectChain.Count > 1,
                             RedirectCount = Math.Max(0, redirectChain.Count - 1),
                             RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                             FailureReason = FetchFailureReasonCode.Unknown
                         };
                    }
                    if (credentialsAllowed)
                    {
                        StoreResponseCookies(resp, topLevelDocumentUri);
                    }
                    
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler
                            current = loc;
                            redirectChain.Add(current);
                            hops++;
                            resp.Dispose();
                            resp = null;
                            /* [PERF-REMOVED] */
                            continue;
                        }
                    }
                    break;
                }

                if (hops >= maxRedirectHops &&
                    resp != null &&
                    (int)resp.StatusCode >= 300 &&
                    (int)resp.StatusCode < 400 &&
                    resp.Headers.Location != null)
                {
                    EngineLogCompat.Warn(
                        $"[Network.Resilience] Redirect hop limit exceeded ({maxRedirectHops}) for '{url}'.",
                        LogCategory.Network);
                    return new FetchResult
                    {
                        Status = FetchStatus.LimitExceeded,
                        ErrorDetail = $"Redirect hop limit exceeded ({maxRedirectHops})",
                        FinalUri = current,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        FailureReason = FetchFailureReasonCode.RedirectLimitExceeded,
                        LimitType = "redirect_hops",
                        IsRetryable = true
                    };
                }

                if (resp == null)
                {
                     return new FetchResult
                     {
                         Status = FetchStatus.ConnectionFailed,
                         ErrorDetail = "No response received",
                         FinalUri = current,
                         Redirected = redirectChain.Count > 1,
                         RedirectCount = Math.Max(0, redirectChain.Count - 1),
                         RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                         FailureReason = FetchFailureReasonCode.TransportFailure,
                         IsRetryable = true
                     };
                }

                var finalUri = resp?.RequestMessage?.RequestUri ?? current ?? url;
                LastTextResponseUri = finalUri;
                var headerSnapshot = SnapshotHeaders(resp);
                // NoteHsts(resp, finalUri ?? url); // Handled by HstsHandler

                var ct = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null;
                
                if (!resp.IsSuccessStatusCode)
                {
                    // 404, 500, etc. Keep diagnostic bodies bounded.
                    string errBody = null;
                    try
                    {
                        const int maxDiagnosticErrorBodyBytes = 64 * 1024;
                        var errBytes = await ReadStreamingBodyBoundedAsync(
                            resp,
                            maxDiagnosticErrorBodyBytes,
                            finalUri?.ToString(),
                            "error_body_bytes").ConfigureAwait(false);
                        errBody = DecodeTextResponse(errBytes, resp.Content?.Headers?.ContentType?.ToString());
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[Network] Failed to read bounded error body for {finalUri}: {ex.Message}", LogCategory.Network);
                    }
                    
                    FetchStatus status = FetchStatus.UnknownError;
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) status = FetchStatus.NotFound;
                    else if ((int)resp.StatusCode >= 500) status = FetchStatus.ConnectionFailed; // Server error

                    return new FetchResult { 
                        Status = status, 
                        StatusCode = (int)resp.StatusCode, 
                        ErrorDetail = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", 
                        Content = errBody,
                        FinalUri = finalUri,
                        ContentType = ct,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        FailureReason = FetchFailureReasonCode.HttpError,
                        IsRetryable = (int)resp.StatusCode >= 500
                    };
                }

                byte[] bodyBytes = null;
                var corbFetchMode = fetchMode;
                var maxTextBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxTextBodyBytes);
                try
                {
                    bodyBytes = await ReadStreamingBodyBoundedAsync(
                        resp,
                        maxTextBodyBytes,
                        finalUri?.ToString(),
                        "text_body_bytes",
                        prefix =>
                        {
                            if (ShouldBlockCorb(
                                corbFetchMode,
                                secFetchDest,
                                refererOriginal,
                                finalUri,
                                resp,
                                prefix.Span,
                                out var reason))
                            {
                                throw new CorbBlockedException(reason);
                            }
                        }).ConfigureAwait(false);
                }
                catch (CorbBlockedException ex)
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = ex.Message,
                        StatusCode = (int)resp.StatusCode,
                        FinalUri = finalUri,
                        ContentType = ct,
                        Headers = resp.Headers,
                        HeaderSnapshot = headerSnapshot,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        FailureReason = FetchFailureReasonCode.CorbBlocked,
                        IsRetryable = false
                    };
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("LIMIT_EXCEEDED"))
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.LimitExceeded,
                        ErrorDetail = $"Response body exceeded max text body bytes ({maxTextBodyBytes}).",
                        FinalUri = finalUri,
                        StatusCode = (int)resp.StatusCode,
                        ContentType = ct,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        FailureReason = FetchFailureReasonCode.LimitExceeded,
                        LimitType = "text_body_bytes",
                        InputSizeBytes = maxTextBodyBytes,
                        IsRetryable = false
                    };
                }
                catch (Exception bodyEx)
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = "Failed to read body: " + bodyEx.Message,
                        FinalUri = finalUri,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        FailureReason = FetchFailureReasonCode.BodyReadFailed,
                        IsRetryable = true
                    };
                }

                var effectiveMime = string.IsNullOrWhiteSpace(ct) || string.Equals(ct, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    ? MimeSniffer.SniffMimeType(bodyBytes, ct)
                    : ct;

                var text = DecodeTextResponse(bodyBytes, resp.Content?.Headers?.ContentType?.ToString());

                if (IsTopLevelDocumentRequest(secFetchDest))
                {
                    FenBrowser.Core.Verification.ContentVerifier.RegisterSource(
                        url?.ToString() ?? "unknown",
                        text?.Length ?? 0,
                        text?.GetHashCode() ?? 0,
                        authoritative: true);
                }

                // Parse X-Frame-Options as directives, not substrings.
                ParseXFrameOptions(resp, out var xFramePolicy, out var xFrameAllowFrom);

                var referrerPolicy = ParseReferrerPolicy(resp);
                AdoptResponseReferrerPolicy(resp, context);

                // Parse COOP/COEP headers for cross-origin isolation state.
                var crossOriginIsolation = new CrossOriginIsolationPolicy();
                if (resp.Headers.TryGetValues("Cross-Origin-Opener-Policy", out var coopValues))
                {
                    crossOriginIsolation.ParseCoopHeader(string.Join(",", coopValues));
                }
                if (resp.Headers.TryGetValues("Cross-Origin-Embedder-Policy", out var coepValues))
                {
                    crossOriginIsolation.ParseCoepHeader(string.Join(",", coepValues));
                }
                string corpHeader = string.Empty;
                if (resp.Headers.TryGetValues("Cross-Origin-Resource-Policy", out var corpValues))
                {
                    corpHeader = string.Join(",", corpValues);
                }

                // Parse Permissions-Policy header (Permissions-Policy spec) with
                // legacy Feature-Policy fallback.
                var permissionsPolicy = Security.PermissionsPolicy.None;
                if (resp.Headers.TryGetValues("Permissions-Policy", out var ppValues))
                {
                    permissionsPolicy = Security.PermissionsPolicy.Parse(string.Join(",", ppValues));
                }
                else if (resp.Headers.TryGetValues("Feature-Policy", out var fpValues))
                {
                    permissionsPolicy = Security.PermissionsPolicy.ParseLegacyFeaturePolicy(string.Join(",", fpValues));
                }

                if (IsTopLevelDocumentRequest(secFetchDest) && crossOriginIsolation.RequiresCorp)
                {
                    crossOriginIsolation.LogState(finalUri);
                }

                if (string.Equals(secFetchDest, "iframe", StringComparison.OrdinalIgnoreCase) &&
                    !IsFrameEmbeddingAllowed(xFramePolicy, xFrameAllowFrom, refererOriginal, finalUri))
                {
                    IncrementBlockedRequestCount();
                    EngineLogCompat.Warn(
                        $"[XFO] Blocked frame embedding for '{finalUri}' due to policy '{xFramePolicy}'" +
                        $"{(string.IsNullOrWhiteSpace(xFrameAllowFrom) ? string.Empty : $" ({xFrameAllowFrom})")}",
                        LogCategory.Network);

                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = "Blocked by X-Frame-Options policy",
                        StatusCode = (int)resp.StatusCode,
                        FinalUri = finalUri,
                        ContentType = effectiveMime,
                        Headers = resp.Headers,
                        HeaderSnapshot = headerSnapshot,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        XFrameOptions = xFramePolicy,
                        XFrameAllowFromUri = xFrameAllowFrom,
                        ReferrerPolicy = referrerPolicy,
                        FailureReason = FetchFailureReasonCode.XFrameBlocked,
                        IsRetryable = false,
                    };
                }

                // Populate the same sharded cache FetchTextAsync uses, so the
                // first request through this detailed path warms the cache for
                // every later request (FetchCssAsync, scripts, preload, ...).
                // Skip top-level documents so the navigated page itself isn't
                // pinned in memory across navigations.
                if (!IsTopLevelDocumentRequest(secFetchDest) && TryGetTextCacheExpiry(resp, out var expiresAtUtc))
                {
                    try
                    {
                        _textCache.Put(cachePartition, cacheKey, new TextEntry
                        {
                            Body = text ?? string.Empty,
                            ContentType = effectiveMime ?? string.Empty,
                            FinalUri = finalUri,
                            StatusCode = (int)resp.StatusCode,
                            HeaderSnapshot = headerSnapshot,
                            Redirected = redirectChain.Count > 1,
                            RedirectCount = Math.Max(0, redirectChain.Count - 1),
                            RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                            XFrameOptions = xFramePolicy,
                            XFrameAllowFromUri = xFrameAllowFrom,
                            ReferrerPolicy = referrerPolicy,
                            CrossOriginIsolation = crossOriginIsolation,
                            CrossOriginResourcePolicy = corpHeader,
                            PermissionsPolicy = permissionsPolicy,
                            ExpiresAtUtc = expiresAtUtc
                        });
                    }
                    catch (Exception cacheEx)
                    {
                        EngineLogCompat.Debug($"[Network] Text cache write failed for {finalUri}: {cacheEx.Message}", LogCategory.Network);
                    }
                }

                return new FetchResult {
                    Status = FetchStatus.Success,
                    Content = text,
                    StatusCode = (int)resp.StatusCode,
                    FinalUri = finalUri,
                    ContentType = effectiveMime,
                    Headers = resp.Headers,
                        HeaderSnapshot = headerSnapshot,
                    Redirected = redirectChain.Count > 1,
                    RedirectCount = Math.Max(0, redirectChain.Count - 1),
                    RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                    XFrameOptions = xFramePolicy,
                    XFrameAllowFromUri = xFrameAllowFrom,
                    ReferrerPolicy = referrerPolicy,
                    CrossOriginIsolation = crossOriginIsolation,
                    CrossOriginResourcePolicy = corpHeader,
                    PermissionsPolicy = permissionsPolicy,
                    DurationMs = (long)(DateTimeOffset.UtcNow - _startFetch).TotalMilliseconds
                };
            }
            catch (Exception ex) {
                return new FetchResult
                {
                    Status = FetchStatus.UnknownError,
                    ErrorDetail = ex.Message,
                    FinalUri = url,
                    Redirected = redirectChain.Count > 1,
                    RedirectCount = Math.Max(0, redirectChain.Count - 1),
                    RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                    FailureReason = FetchFailureReasonCode.Unknown,
                    IsRetryable = false
                };
            }
        }

        // Extended variant with explicit UA and Accept-Encoding overrides for multi-strategy fallback
        public async Task<string> FetchTextWithOptionsAsync(Uri url, Uri referer, string accept, string secFetchDest, string userAgentOverride, string acceptEncodingOverride)
        {
            if (url == null) return null;
            LastTextResponseUri = null;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaderSafe(req, "Accept", string.IsNullOrWhiteSpace(accept) ? "*/*" : accept);
                AddHeaderSafe(req, "User-Agent", string.IsNullOrWhiteSpace(userAgentOverride) ? "Mozilla/5.0" : userAgentOverride);
                AddHeaderSafe(req, "Accept-Language", "en-US,en;q=0.9");
                AddHeaderSafe(req, "Accept-Encoding", string.IsNullOrWhiteSpace(acceptEncodingOverride) ? "gzip, deflate" : acceptEncodingOverride);
                AddHeaderSafe(req, "Sec-Fetch-Dest", string.IsNullOrWhiteSpace(secFetchDest) ? "empty" : secFetchDest);
                var fetchMode = "cors";
                var destLower = (secFetchDest ?? string.Empty).ToLowerInvariant();
                if (destLower == "document" || destLower == "iframe") fetchMode = "navigate";
                else if (destLower == "style" || destLower == "script" || destLower == "image" || destLower == "font") fetchMode = "no-cors";
                AddHeaderSafe(req, "Sec-Fetch-Mode", fetchMode);
                var computedReferer = ComputeReferrerHeader(referer, url, ActiveReferrerPolicy);
                ApplyRefererHeader(req, referer, url, ActiveReferrerPolicy);
                AddHeaderSafe(req, "Sec-Fetch-Site", BrowserRequestHeaderPolicy.DetermineSite(computedReferer, url));
                ApplyNavigationRequestHeaders(req, secFetchDest);
                AttachCookies(req, referer ?? url, secFetchDest);
                var cts = new System.Threading.CancellationTokenSource();
                try { cts.CancelAfter(TimeSpan.FromSeconds(30)); }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[Network] Failed to apply optimized-fetch timeout: {ex.Message}", LogCategory.Network);
                }
                HttpResponseMessage resp = null;
                try
                {
                    resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false);
                }
                catch (Exception sendEx)
                {
                    EngineLogCompat.Debug($"[FetchTextOptError] send {url} ex={sendEx.Message}", LogCategory.Network);
                }
                StoreResponseCookies(resp, referer ?? url);
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    EngineLogCompat.Debug($"[FetchTextOptFail] url={url} status={(resp != null ? (int)resp.StatusCode : 0)}", LogCategory.Network);
                    return null;
                }
                if (IsTopLevelDocumentRequest(secFetchDest))
                {
                    ActiveReferrerPolicy = ParseReferrerPolicy(resp);
                }
                LastTextResponseUri = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : url;
                string text = null;
                byte[] bodyBytes = null;
                var maxBinaryBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxTextBodyBytes);
                try
                {
                    bodyBytes = await ReadStreamingBodyBoundedAsync(resp, maxBinaryBodyBytes, url?.ToString(), "text_body_bytes").ConfigureAwait(false);
                    text = System.Text.Encoding.UTF8.GetString(bodyBytes);
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("LIMIT_EXCEEDED"))
                {
                    EngineLogCompat.Debug($"[FetchTextOptError] body limit exceeded {url}", LogCategory.Network);
                    return null;
                }
                catch (Exception bodyEx)
                {
                    EngineLogCompat.Debug($"[FetchTextOptError] body {url} ex={bodyEx.Message}", LogCategory.Network);
                }
                if (string.IsNullOrEmpty(text))
                {
                    EngineLogCompat.Debug($"[FetchTextOptEmpty] url={url}", LogCategory.Network);
                }
                return text;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[FetchTextOptError] url={url} ex={ex.Message}", LogCategory.Network);
                return null;
            }
        }

        // Image with redirect and memory cache; disk caching optional later
        public Task<Stream> FetchImageAsync(Uri url, Uri referer = null)
        {
            return FetchImageAsync(new FetchContext
            {
                RequestUri = url,
                InitiatorUri = referer,
                FrameDocumentUri = referer,
                TopLevelDocumentUri = referer,
                Destination = "image",
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            });
        }

        public async Task<Stream> FetchImageAsync(FetchContext context)
        {
            if (context == null) return null;
            var url = context.RequestUri;
            var referer = context.InitiatorUri ?? context.FrameDocumentUri;
            var topLevelDocumentUri = context.TopLevelDocumentUri ?? referer;
            if (url == null) return null;
            if (!IsSupportedFetchScheme(url))
            {
                EngineLogCompat.Warn($"[FetchImage] Blocked unsupported scheme '{url.Scheme}' for {url}", LogCategory.Security);
                return null;
            }
            if (referer != null &&
                referer.IsAbsoluteUri &&
                url.IsAbsoluteUri &&
                string.Equals(referer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                IncrementBlockedRequestCount();
                EngineLogCompat.Warn($"[MixedContent] Blocked insecure image '{url}' from secure document '{referer}'", LogCategory.Network);
                return null;
            }

            // CSP Check
            if (ActivePolicy != null && !ActivePolicy.IsAllowed("img-src", url, ExtractOrigin(referer)))
            {
                 System.Diagnostics.Debug.WriteLine($"[CSP] Blocked image {url}");
                 return null;
            }

            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsFileSchemeAccessAllowed())
                {
                    EngineLogCompat.Warn($"[FetchImage] Blocked file scheme load by policy: {url}", LogCategory.Security);
                    return null;
                }

                try
                {
                    return File.OpenRead(url.LocalPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchImage] file failed: {url} {ex.Message}");
                    return null;
                }
            }
            
            // Handle internal fen:// scheme - these are browser internal URLs, not fetchable
            if (string.Equals(url.Scheme, "fen", StringComparison.OrdinalIgnoreCase))
            {
                // Internal URLs like fen://newtab/favicon.ico are not HTTP fetchable
                // The UI layer should handle these with embedded resources
                return null;
            }

            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                var maxImageBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxImageBodyBytes);
                if (!DataUrlParser.TryParse(url, maxImageBodyBytes, out var dataUrl, out var dataUrlError))
                {
                    EngineLogCompat.Debug($"[FetchImage] data URL rejected: {dataUrlError}", LogCategory.Network);
                    return null;
                }

                return new MemoryStream(dataUrl.Bytes, writable: false);
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            var key = url.AbsoluteUri;

            // Sharded Lookup
            string partition = context.NetworkPartitionKey.ToStorageKey();
            if (_imgCache.TryGet(partition, key, out var imgEntry))
            {
                if (imgEntry.Buffer != null)
                {
                    return new MemoryStream(imgEntry.Buffer);
                }
            }

            var refererOriginal = referer;
            Uri previousRequest = null;

                try
                {
                    var _startImg = DateTimeOffset.UtcNow;
                    Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                var maxRedirectHops = Math.Max(1, GetResilienceSettings().MaxRedirectHops);
                while (hops < maxRedirectHops)
                {
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    var effectiveReferer = refererOriginal ?? previousRequest;
                    BrowserRequestHeaderPolicy.Apply(
                        req,
                        context,
                        context.ReferrerPolicy ?? ActiveReferrerPolicy,
                        BrowserNetworkCapabilities.ImageAcceptHeader,
                        effectiveReferer,
                        acceptEncoding: "gzip, deflate");
                    AttachCookies(req, topLevelDocumentUri ?? current, "image");
                    var cts = new System.Threading.CancellationTokenSource();
                    try { cts.CancelAfter(System.TimeSpan.FromSeconds(30)); }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[FetchImage] Failed to apply timeout: {ex.Message}", LogCategory.Network);
                    }
                    resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false);
                    StoreResponseCookies(resp, topLevelDocumentUri ?? current);
                    
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler
                            current = loc;
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    /* [PERF-REMOVED] */
                    return null;
                }
                // NoteHsts(resp, url); // Handled by HstsHandler

                var maxImageBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxImageBodyBytes);
                byte[] buf;
                try
                {
                    buf = await ReadStreamingBodyBoundedAsync(
                        resp,
                        maxImageBodyBytes,
                        url?.ToString(),
                        "image_body_bytes").ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("LIMIT_EXCEEDED"))
                {
                    return null;
                }
                var finalUri = resp?.RequestMessage?.RequestUri ?? current ?? url;
                if (ShouldBlockCorb(
                    "no-cors",
                    "image",
                    refererOriginal,
                    finalUri,
                    resp,
                    buf.AsSpan(0, Math.Min(buf.Length, 512)),
                    out _))
                {
                    return null;
                }

                var entry = new ImgEntry { Buffer = buf, ContentType = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null };
                
                // Phase 2.3: Sharded Image Cache
                string partitionKey = context.NetworkPartitionKey.ToStorageKey();
                _imgCache.Put(partitionKey, key, entry);

                return new MemoryStream(buf);
            }
            catch (Exception ex) {
                var msg = $"[FetchImageException] url={url} ex={ex.Message}";
                EngineLogCompat.Debug(msg, LogCategory.Network);
                try
                {
                    LogSink?.Invoke(msg);
                }
                catch (Exception sinkEx)
                {
                    EngineLogCompat.Debug($"[Network] FetchImage exception sink failed: {sinkEx.Message}", LogCategory.Network);
                }
                return null;
            }
        }

        // Generic binary fetcher for fonts and other non-text assets
        public Task<byte[]> FetchBytesAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            var destination = string.IsNullOrWhiteSpace(secFetchDest) ? "empty" : secFetchDest;
            return FetchBytesAsync(
                new FetchContext
                {
                    RequestUri = url,
                    InitiatorUri = referer,
                    FrameDocumentUri = referer,
                    TopLevelDocumentUri = referer,
                    Destination = destination,
                    Mode = BrowserRequestHeaderPolicy.DetermineMode(destination),
                    CredentialsMode = "include",
                    Method = "GET"
                },
                accept);
        }

        public async Task<byte[]> FetchBytesAsync(FetchContext context, string accept = null)
        {
            var result = await FetchBytesDetailedAsync(context, accept).ConfigureAwait(false);
            return result.Body;
        }

        public async Task<BinaryFetchResult> FetchBytesDetailedAsync(FetchContext context, string accept = null)
        {
            if (context == null)
            {
                return BinaryFailure(BinaryFetchFailureReason.InvalidRequest, null, "Fetch context is null");
            }
            var url = context.RequestUri;
            var referer = context.InitiatorUri ?? context.FrameDocumentUri;
            var topLevelDocumentUri = context.TopLevelDocumentUri ?? referer;
            var secFetchDest = context.Destination;
            if (url == null)
            {
                return BinaryFailure(BinaryFetchFailureReason.InvalidRequest, null, "Request URI is null");
            }
            if (!IsSupportedFetchScheme(url))
            {
                EngineLogCompat.Warn($"[FetchBytes] Blocked unsupported scheme '{url.Scheme}' for {url}", LogCategory.Security);
                return BinaryFailure(BinaryFetchFailureReason.UnsupportedScheme, url, $"Unsupported scheme: {url.Scheme}");
            }
            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                var maxDataBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxImageBodyBytes);
                if (!DataUrlParser.TryParse(url, maxDataBodyBytes, out var dataUrl, out var dataUrlError))
                {
                    return BinaryFailure(
                        BinaryFetchFailureReason.InvalidRequest,
                        url,
                        dataUrlError,
                        bodySizeAllowed: !dataUrlError.Contains("limit", StringComparison.OrdinalIgnoreCase));
                }

                return new BinaryFetchResult
                {
                    Body = dataUrl.Bytes,
                    FinalUri = url,
                    RedirectChain = new[] { url },
                    ContentType = dataUrl.ContentType
                };
            }
            if (string.Equals(secFetchDest, "image", StringComparison.OrdinalIgnoreCase) &&
                referer != null &&
                referer.IsAbsoluteUri &&
                url.IsAbsoluteUri &&
                string.Equals(referer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                IncrementBlockedRequestCount();
                EngineLogCompat.Warn($"[MixedContent] Blocked insecure image bytes fetch '{url}' from secure document '{referer}'", LogCategory.Network);
                return BinaryFailure(BinaryFetchFailureReason.MixedContentBlocked, url, "Blocked mixed-content image request");
            }
            
            // CSP Check (fonts, media, etc)
            if (ActivePolicy != null)
            {
                var directive = "default-src";
                if (secFetchDest == "font") directive = "font-src";
                else if (secFetchDest == "audio" || secFetchDest == "video") directive = "media-src";
                else if (secFetchDest == "object") directive = "object-src";
                
                if (!ActivePolicy.IsAllowed(directive, url, ExtractOrigin(referer)))
                {
                    return BinaryFailure(BinaryFetchFailureReason.CspBlocked, url, $"Blocked by {directive}", cspAllowed: false);
                }
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            try
            {
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                var redirectChain = new List<Uri> { url };
                var maxRedirectHops = Math.Max(1, GetResilienceSettings().MaxRedirectHops);
                while (hops < maxRedirectHops)
                {
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    BrowserRequestHeaderPolicy.Apply(
                        req,
                        context,
                        context.ReferrerPolicy ?? ActiveReferrerPolicy,
                        string.IsNullOrWhiteSpace(accept) ? "*/*" : accept,
                        referer);
                    AttachCookies(req, topLevelDocumentUri ?? current, secFetchDest);
                    var cts = new System.Threading.CancellationTokenSource();
                    try
                    {
                        int sec = ResolveTimeoutSeconds(secFetchDest);
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[FetchBytes] Failed to apply timeout: {ex.Message}", LogCategory.Network);
                    }
                    resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false);
                    StoreResponseCookies(resp, topLevelDocumentUri ?? current);
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            var prev = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler
                            current = loc;
                            redirectChain.Add(current);
                            referer = prev;
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null)
                {
                    return BinaryFailure(BinaryFetchFailureReason.TransportFailure, current, "No response received", redirectChain);
                }
                if (hops >= maxRedirectHops && (int)resp.StatusCode is >= 300 and < 400)
                {
                    return BinaryFailure(BinaryFetchFailureReason.RedirectLimitExceeded, current, "Redirect limit exceeded", redirectChain, (int)resp.StatusCode);
                }
                // NoteHsts(resp, url); // Handled by HstsHandler

                bool allowBodyOnError = !resp.IsSuccessStatusCode && ShouldAllowBinaryBodyOnHttpError(secFetchDest, resp);
                if (!resp.IsSuccessStatusCode && !allowBodyOnError)
                {
                    EngineLogCompat.Warn(
                        $"[FetchBytes] HTTP {(int)resp.StatusCode} for image '{url}' (Content-Type: {resp.Content?.Headers?.ContentType?.MediaType ?? "none"}) — body not allowed on error",
                        LogCategory.Network);
                    return BinaryFailure(BinaryFetchFailureReason.HttpError, current, $"HTTP {(int)resp.StatusCode}", redirectChain, (int)resp.StatusCode, resp.Content?.Headers?.ContentType?.MediaType);
                }

                var maxBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxImageBodyBytes);
                byte[] buf;
                try
                {
                    buf = await ReadStreamingBodyBoundedAsync(
                        resp,
                        maxBodyBytes,
                        current?.ToString(),
                        "binary_body_bytes").ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("LIMIT_EXCEEDED"))
                {
                    return BinaryFailure(
                        BinaryFetchFailureReason.BodySizeLimitExceeded,
                        current,
                        $"Body exceeded limit {maxBodyBytes}",
                        redirectChain,
                        (int)resp.StatusCode,
                        resp.Content?.Headers?.ContentType?.MediaType,
                        bodySizeAllowed: false);
                }
                catch (Exception ex)
                {
                    return BinaryFailure(
                        BinaryFetchFailureReason.BodyReadFailed,
                        current,
                        ex.Message,
                        redirectChain,
                        (int)resp.StatusCode,
                        resp.Content?.Headers?.ContentType?.MediaType);
                }
                var finalUri = resp?.RequestMessage?.RequestUri ?? current ?? url;
                if (ShouldBlockCorb(
                    "no-cors",
                    secFetchDest,
                    context.InitiatorUri ?? context.FrameDocumentUri,
                    finalUri,
                    resp,
                    buf.AsSpan(0, Math.Min(buf.Length, 512)),
                    out _))
                {
                    return BinaryFailure(
                        BinaryFetchFailureReason.CorbBlocked,
                        finalUri,
                        "Response blocked by CORB",
                        redirectChain,
                        (int)resp.StatusCode,
                        resp.Content?.Headers?.ContentType?.MediaType,
                        corbAllowed: false);
                }
                return new BinaryFetchResult
                {
                    Body = buf,
                    StatusCode = (int)resp.StatusCode,
                    FinalUri = finalUri,
                    RedirectChain = redirectChain,
                    ContentType = resp.Content?.Headers?.ContentType?.MediaType,
                    ResponseHeaders = SafeBinaryResponseHeaders(resp)
                };
            }
            catch (TaskCanceledException ex)
            {
                return BinaryFailure(BinaryFetchFailureReason.Timeout, url, ex.Message);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[FetchBytes] Failed for {url}: {ex.Message}", LogCategory.Network);
                return BinaryFailure(BinaryFetchFailureReason.TransportFailure, url, ex.Message);
            }
        }

        private static BinaryFetchResult BinaryFailure(
            BinaryFetchFailureReason reason,
            Uri finalUri,
            string detail,
            IReadOnlyList<Uri> redirectChain = null,
            int statusCode = 0,
            string contentType = null,
            bool cspAllowed = true,
            bool corbAllowed = true,
            bool bodySizeAllowed = true) => new BinaryFetchResult
            {
                FailureReason = reason,
                FailureDetail = detail,
                FinalUri = finalUri,
                RedirectChain = redirectChain ?? Array.Empty<Uri>(),
                StatusCode = statusCode,
                ContentType = contentType,
                CspAllowed = cspAllowed,
                CorbAllowed = corbAllowed,
                BodySizeAllowed = bodySizeAllowed
            };

        private static IReadOnlyDictionary<string, string> SafeBinaryResponseHeaders(HttpResponseMessage response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (response?.Content?.Headers?.ContentEncoding?.Count > 0)
            {
                headers["Content-Encoding"] = string.Join(",", response.Content.Headers.ContentEncoding);
            }
            if (response?.Content?.Headers?.ContentLength is long contentLength)
            {
                headers["Content-Length"] = contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return headers;
        }

        private static bool ShouldAllowBinaryBodyOnHttpError(string secFetchDest, HttpResponseMessage response)
        {
            var normalizedDest = (secFetchDest ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedDest != "image" && normalizedDest != "object")
            {
                return false;
            }

            string mediaType = response?.Content?.Headers?.ContentType?.MediaType ?? string.Empty;
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return false;
            }

            return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                   mediaType.Equals("application/svg+xml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sends a generic HTTP request with CSP checks and standard headers.
        /// Used by Fetch API and other generic networking needs.
        /// </summary>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CspPolicy policy)
        {
            if (request == null || request.RequestUri == null) throw new ArgumentNullException(nameof(request));
            var referrer = request.Headers.Referrer;
            return SendAsync(request, policy, new FetchContext
            {
                RequestUri = request.RequestUri,
                InitiatorUri = referrer,
                FrameDocumentUri = referrer,
                TopLevelDocumentUri = referrer,
                Destination = GetHeaderValue(request.Headers, "Sec-Fetch-Dest") ?? "empty",
                Mode = GetHeaderValue(request.Headers, "Sec-Fetch-Mode") ?? "cors",
                CredentialsMode = "include",
                Method = request.Method.Method
            });
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CspPolicy policy,
            FetchContext context)
        {
            if (request == null || request.RequestUri == null) throw new ArgumentNullException(nameof(request));
            if (context == null) throw new ArgumentNullException(nameof(context));
            context = context with { RequestUri = request.RequestUri, Method = request.Method.Method };
            BrowserRequestHeaderPolicy.Apply(
                request,
                context,
                context.ReferrerPolicy ?? ActiveReferrerPolicy,
                request.Headers.Accept.Count > 0 ? null : "*/*",
                context.InitiatorUri ?? context.FrameDocumentUri);
            var requestUrl = request.RequestUri.AbsoluteUri;
            var logContext = new EngineLogContext(
                NavigationId: LogContext.CurrentCorrelationId,
                Url: requestUrl,
                ResourceUrl: requestUrl,
                SpecArea: "fetch-cors-csp",
                Source: nameof(ResourceManager));

            // CSP Check
            if (policy != null)
            {
                // Default to connect-src for generic fetch/XHR
                if (!policy.IsAllowed("connect-src", request.RequestUri, ExtractOrigin(request.Headers.Referrer)))
                {
                    FailClosedDiagnostics.LogDenied(
                        capabilityId: "SECURITY-CSP-ENFORCEMENT-01",
                        stage: "fetch.csp",
                        reasonCode: FailClosedReasonCodes.CspConnectSrcBlocked,
                        message: $"[CSP] Blocked generic request to {request.RequestUri} (connect-src)",
                        subsystem: LogSubsystem.Security,
                        context: logContext,
                        fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["directive"] = "connect-src",
                            ["requestMethod"] = request.Method.Method,
                            ["requestUri"] = requestUrl
                        });
throw new HttpRequestException($"Blocked by Content Security Policy (connect-src): {request.RequestUri}");
                }
            }

            // Mixed Content Check
            if (policy != null)
            {
                var topLevelUri = context.TopLevelDocumentUri ?? context.FrameDocumentUri ?? context.InitiatorUri;
                if (topLevelUri != null)
                {
                    var fetchDest = GetHeaderValue(request.Headers, "Sec-Fetch-Dest") ?? "empty";
                    var isUpgradeInsecureRequests = policy?.HasUpgradeInsecureRequests() ?? false;
                    var mixedContentDecision = MixedContentChecker.CheckMixedContent(
                        request.RequestUri,
                        topLevelUri,
                        fetchDest,
                        isUpgradeInsecureRequestsEnabled: isUpgradeInsecureRequests);

                    if (mixedContentDecision.IsBlocked)
                    {
                        FailClosedDiagnostics.LogDenied(
                            capabilityId: "SECURITY-MIXED-CONTENT-01",
                            stage: "fetch.mixed-content",
                            reasonCode: FailClosedReasonCodes.MixedContentBlocked,
                            message: $"[MixedContent] {mixedContentDecision.Reason}",
                            subsystem: LogSubsystem.Security,
                            context: logContext,
                            fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["requestType"] = GetHeaderValue(request.Headers, "Sec-Fetch-Dest") ?? "empty",
                                ["requestUri"] = requestUrl,
                                ["pageUri"] = topLevelUri.AbsoluteUri
                            });
                        throw new HttpRequestException($"Blocked by Mixed Content Policy: {mixedContentDecision.Reason}");
                    }
                    else if (mixedContentDecision.IsUpgraded)
                    {
                        // Upgrade the request to HTTPS
                        request.RequestUri = mixedContentDecision.UpgradedUrl;
                        EngineLogCompat.Info($"[MixedContent] Upgraded insecure request to HTTPS: {mixedContentDecision.UpgradedUrl}", LogCategory.Security);
                    }
                }
            }

            try
            {
                // Go through INetworkClient pipeline (handles cookies, HSTS, tracking prevention)
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var fetchMode = GetHeaderValue(request.Headers, "Sec-Fetch-Mode");
                var isCorsMode = string.Equals(fetchMode, "cors", StringComparison.OrdinalIgnoreCase);
                var originUri = GetCorsOrigin(request);
                if (isCorsMode && originUri == null)
                {
                    FailClosedDiagnostics.LogDenied(
                        capabilityId: "FETCH-CORS-POLICY-01",
                        stage: "fetch.cors",
                        reasonCode: FailClosedReasonCodes.CorsOriginContextMissing,
                        message: $"[CORS] Blocked request to {request.RequestUri} due to missing origin context",
                        subsystem: LogSubsystem.Security,
                        context: logContext,
                        fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["fetchMode"] = fetchMode ?? string.Empty,
                            ["requestMethod"] = request.Method.Method,
                            ["requestUri"] = requestUrl
                        });
                    throw new HttpRequestException($"Blocked by CORS policy (missing origin context): {request.RequestUri}");
                }

                var topLevelDocumentUri = context.TopLevelDocumentUri ?? context.FrameDocumentUri ?? context.InitiatorUri ?? request.RequestUri;
                var credentialsAllowed = AreCredentialsAllowed(context, request.RequestUri);
                if (credentialsAllowed)
                {
                    AttachCookies(request, topLevelDocumentUri, GetHeaderValue(request.Headers, "Sec-Fetch-Dest"));
                }
                ApplyCorsOriginHeader(request, originUri);
                await EnsureCorsPreflightAsync(request, originUri, cts.Token).ConfigureAwait(false);
                var response = await SendRequestTrackedAsync(request, cts.Token).ConfigureAwait(false);
                if (isCorsMode && !CorsHandler.IsCorsAllowed(response, request.RequestUri, originUri))
                {
                    FailClosedDiagnostics.LogDenied(
                        capabilityId: "FETCH-CORS-POLICY-01",
                        stage: "fetch.cors-response",
                        reasonCode: FailClosedReasonCodes.CorsResponseDisallowed,
                        message: $"[CORS] Blocked response from {request.RequestUri} due to missing/invalid ACAO",
                        subsystem: LogSubsystem.Security,
                        context: logContext,
                        fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["requestMethod"] = request.Method.Method,
                            ["requestUri"] = requestUrl,
                            ["originUri"] = originUri?.AbsoluteUri ?? string.Empty
                        });
                    response.Dispose();
                    throw new HttpRequestException($"Blocked by CORS policy (response validation failed): {request.RequestUri}");
                }

                if (credentialsAllowed)
                {
                    StoreResponseCookies(response, topLevelDocumentUri);
                }
                return response;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Error($"[ResourceManager] SendAsync failed: {ex.Message}", LogCategory.Network);
                throw;
            }
        }

        private static Uri GetCorsOrigin(HttpRequestMessage request)
        {
            if (CorsHandler.TryGetOriginUri(request, out var headerOrigin))
            {
                return headerOrigin;
            }

            return ExtractOrigin(request.Headers.Referrer);
        }

        private static bool AreCredentialsAllowed(FetchContext context, Uri requestUri)
        {
            var credentialsMode = (context?.CredentialsMode ?? "same-origin").Trim();
            return string.Equals(credentialsMode, "include", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(credentialsMode, "same-origin", StringComparison.OrdinalIgnoreCase) &&
                 context?.InitiatorUri != null &&
                 CorsHandler.IsSameOrigin(context.InitiatorUri, requestUri));
        }

        private static void ApplyCorsOriginHeader(HttpRequestMessage request, Uri originUri)
        {
            if (request?.RequestUri == null || originUri == null || CorsHandler.IsSameOrigin(request.RequestUri, originUri))
            {
                return;
            }

            if (request.Headers.Contains("Origin"))
            {
                return;
            }

            var originHeader = CorsHandler.SerializeOrigin(originUri);
            if (!string.IsNullOrWhiteSpace(originHeader))
            {
                request.Headers.TryAddWithoutValidation("Origin", originHeader);
            }
        }

        private async Task EnsureCorsPreflightAsync(HttpRequestMessage request, Uri originUri, CancellationToken token)
        {
            if (!CorsHandler.RequiresPreflight(request, originUri))
            {
                return;
            }

            using var preflight = new HttpRequestMessage(HttpMethod.Options, request.RequestUri);
            var originHeader = CorsHandler.SerializeOrigin(originUri);
            if (!string.IsNullOrWhiteSpace(originHeader))
            {
                preflight.Headers.TryAddWithoutValidation("Origin", originHeader);
            }

            preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", request.Method.Method.ToUpperInvariant());
            var requestedHeaders = CorsHandler.GetCorsUnsafeRequestHeaderNames(request);
            if (requestedHeaders.Count > 0)
            {
                preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", string.Join(", ", requestedHeaders));
            }

            if (!preflight.Headers.Contains("User-Agent"))
            {
                preflight.Headers.Add("User-Agent", BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent));
            }

            preflight.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            preflight.Headers.TryAddWithoutValidation("Sec-Fetch-Site", BrowserRequestHeaderPolicy.DetermineSite(originUri, request.RequestUri));
            preflight.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

            using var preflightResponse = await SendRequestTrackedAsync(preflight, token).ConfigureAwait(false);
            if (CorsHandler.IsPreflightAllowed(preflightResponse, request, originUri))
            {
                return;
            }

            var requestUrl = request?.RequestUri?.AbsoluteUri ?? string.Empty;
            FailClosedDiagnostics.LogDenied(
                capabilityId: "FETCH-CORS-POLICY-01",
                stage: "fetch.cors-preflight",
                reasonCode: FailClosedReasonCodes.CorsPreflightBlocked,
                message: $"[CORS] Preflight blocked {request.Method.Method} {request.RequestUri}",
                subsystem: LogSubsystem.Security,
                context: new EngineLogContext(
                    NavigationId: LogContext.CurrentCorrelationId,
                    Url: requestUrl,
                    ResourceUrl: requestUrl,
                    SpecArea: "fetch-cors-preflight",
                    Source: nameof(ResourceManager)),
                fields: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["requestMethod"] = request.Method.Method,
                    ["requestUri"] = requestUrl,
                    ["originUri"] = originUri?.AbsoluteUri ?? string.Empty
                });
            throw new HttpRequestException($"Blocked by CORS preflight: {request.RequestUri}");
        }

        private async Task<HttpResponseMessage> SendRequestTrackedAsync(HttpRequestMessage req, CancellationToken token)
        {
            var id = System.Threading.Interlocked.Increment(ref _nextRequestId);

            try
            {
                /* [PERF-REMOVED] */
                PublishNetworkEvent(NetworkRequestStarting, id, req, "starting");
                
                var resp = await _client.SendAsync(req, token).ConfigureAwait(false);
                
                /* [PERF-REMOVED] */
                PublishNetworkEvent(NetworkRequestCompleted, id, resp, "completed");
                return resp;
            }
            catch (Exception ex)
            {
                /* [PERF-REMOVED] */
                PublishNetworkEvent(NetworkRequestFailed, id, ex, "failed");
                throw;
            }
        }

        private static void PublishNetworkEvent<T>(Action<string, T> listeners, long requestId, T payload, string phase)
        {
            if (listeners == null)
            {
                return;
            }

            var externalId = requestId.ToString(CultureInfo.InvariantCulture);
            foreach (Action<string, T> listener in listeners.GetInvocationList())
            {
                try
                {
                    listener(externalId, payload);
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug(
                        $"[Network] Request {phase} subscriber failed: {ex.GetType().Name}",
                        LogCategory.Network);
                }
            }
        }
        public Task<string> FetchCssAsync(Uri url)
        {
            if (url == null) return Task.FromResult<string>(null);
            return FetchCssAsync(new FetchContext
            {
                RequestUri = url,
                Destination = "style",
                Mode = "no-cors",
                CredentialsMode = "include",
                Method = "GET"
            });
        }

        public async Task<string> FetchCssAsync(FetchContext context)
        {
            if (context?.RequestUri == null) return null;
            var url = context.RequestUri;
            // Use FetchTextDetailedAsync to inspect headers before returning
            var result = await FetchTextDetailedAsync(
                context with
                {
                    Destination = "style",
                    Mode = string.IsNullOrWhiteSpace(context.Mode) ? "no-cors" : context.Mode,
                    Method = "GET"
                },
                accept: "text/css,*/*;q=0.1");

            if (result.Status != FetchStatus.Success)
            {
                EngineLogCompat.Warn($"[CssLoader] CSS Fetch Failed: {url} Status: {result.Status} Detail: {result.ErrorDetail}", LogCategory.Network);
                return null; 
            }

            // Mime Check
            var ct = result.ContentType;
            if (!string.IsNullOrWhiteSpace(ct))
            {
                ct = ct.ToLowerInvariant();
                bool isCssMime =
                    ct.Contains("text/css") ||
                    ct.EndsWith("+css", StringComparison.OrdinalIgnoreCase);

                // If it is explicitly JAVASCRIPT, we reject it
                // Google serves 'xjs' as text/javascript which contains valid-looking tokens but is not CSS.
                if (ct.Contains("javascript") || ct.Contains("ecmascript"))
                {
                    System.Diagnostics.Debug.WriteLine($"[CssLoader] BLOCKED JS masquerading as CSS: {url} ({ct})");
                    EngineLogCompat.Warn($"[CssLoader] Blocked non-CSS resource: {url} Content-Type: {ct}", LogCategory.Network);
                    return null;
                }

                // Acid3 deliberately serves HTML from empty.css. If we accept explicit document MIME
                // types here, the body turns red because HTML text is misparsed as author CSS.
                bool isDocumentMime =
                    ct.Contains("text/html") ||
                    ct.Contains("application/xhtml+xml") ||
                    ct.Contains("application/xml") ||
                    ct.Contains("text/xml") ||
                    ct.Contains("image/svg+xml");

                if (isDocumentMime && !isCssMime)
                {
                    EngineLogCompat.Warn($"[CssLoader] Blocked document resource masquerading as CSS: {url} Content-Type: {ct}", LogCategory.Network);
                    return null;
                }
            }

            // X-Content-Type-Options: nosniff — if set, the content-type MUST be text/css
            if (result.TryGetHeaderValues("X-Content-Type-Options", out var xctoVals))
            {
                var xcto = string.Join(",", xctoVals).Trim().ToLowerInvariant();
                if (xcto.Contains("nosniff"))
                {
                    var ctCheck = result.ContentType?.ToLowerInvariant() ?? "";
                    if (!ctCheck.Contains("text/css"))
                    {
                        EngineLogCompat.Warn($"[nosniff] Blocked stylesheet — Content-Type '{result.ContentType}' not text/css: {url}", LogCategory.Network);
                        return null;
                    }
                }
            }

            EngineLogCompat.Debug($"[CssLoader] CSS Fetch Success: {url} Length: {result.Content?.Length ?? 0} Type: {result.ContentType}", LogCategory.Network);
            return result.Content;
        }
        private async Task<byte[]> ReadStreamingBodyBoundedAsync(
            HttpResponseMessage resp,
            long maxSize,
            string uri,
            string limitType,
            Action<ReadOnlyMemory<byte>> classifyPrefix = null)
        {
            if (resp.Content == null) return Array.Empty<byte>();
            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var declaredLength = resp.Content.Headers.ContentLength;
            if (declaredLength is >= 0 and <= int.MaxValue && declaredLength <= maxSize)
            {
                var offset = 0;
                byte[] prefix = null;
                if (classifyPrefix != null)
                {
                    prefix = new byte[Math.Min(512, (int)declaredLength.Value)];
                    while (offset < prefix.Length)
                    {
                        var read = await stream.ReadAsync(prefix.AsMemory(offset)).ConfigureAwait(false);
                        if (read == 0)
                        {
                            Array.Resize(ref prefix, offset);
                            break;
                        }

                        offset += read;
                    }

                    classifyPrefix(prefix);
                }

                var result = new byte[(int)declaredLength.Value];
                if (prefix != null)
                {
                    prefix.CopyTo(result, 0);
                }

                while (offset < result.Length)
                {
                    var read = await stream.ReadAsync(result.AsMemory(offset)).ConfigureAwait(false);
                    if (read == 0)
                    {
                        Array.Resize(ref result, offset);
                        break;
                    }

                    offset += read;
                }

                return result;
            }

            var initialCapacity = declaredLength is > 0 and <= int.MaxValue
                ? (int)declaredLength.Value
                : 0;
            using var output = initialCapacity > 0 ? new MemoryStream(initialCapacity) : new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            var prefixClassified = classifyPrefix == null;
            try
            {
                while (true)
                {
                    var readSize = prefixClassified ? buffer.Length : Math.Min(512, buffer.Length);
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, readSize)).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (output.Length + bytesRead > maxSize)
                    {
                        EngineLogCompat.Warn($"[Network.Resilience] Body limit exceeded for '{uri}' (> {maxSize}).", LogCategory.Network);
                        throw new InvalidOperationException($"LIMIT_EXCEEDED:{maxSize}:{output.Length + bytesRead}");
                    }

                    output.Write(buffer, 0, bytesRead);
                    if (!prefixClassified && output.Length >= 512)
                    {
                        classifyPrefix(output.GetBuffer().AsMemory(0, Math.Min((int)output.Length, 512)));
                        prefixClassified = true;
                    }
                }

                if (!prefixClassified)
                {
                    classifyPrefix(ReadOnlyMemory<byte>.Empty);
                }

                return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private sealed class CorbBlockedException : Exception
        {
            public CorbBlockedException(string message)
                : base(message)
            {
            }
        }
    }
}
