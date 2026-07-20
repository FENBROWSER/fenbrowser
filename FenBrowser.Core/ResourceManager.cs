using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
using FenBrowser.Core.Compat;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Security;
using FenBrowser.Core.Security.Corb;
using FenBrowser.Core.Storage;
using System.Net.Http.Headers;

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
    }

    public sealed class ResourceManager
    {
        public static System.Action<string> LogSink;

        // Phase 2.3: Sharded Caches
        public sealed class TextEntry { public string Body; public string ContentType; }
        private readonly FenBrowser.Core.Cache.ShardedCache<TextEntry> _textCache = new FenBrowser.Core.Cache.ShardedCache<TextEntry>(128);

        public sealed class ImgEntry { public byte[] Buffer; public string ContentType; }
        private readonly FenBrowser.Core.Cache.ShardedCache<ImgEntry> _imgCache = new FenBrowser.Core.Cache.ShardedCache<ImgEntry>(64);

        public Uri LastTextResponseUri { get; private set; }
        public ReferrerPolicyDirective ActiveReferrerPolicy { get; private set; } = ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;



        private readonly string _cacheRoot;
        private readonly INetworkClient _client;

        public int BlockedRequestCount { get; private set; }
        public event EventHandler<int> BlockedCountChanged;
        
        // DevTools Network Monitoring Events
        public event Action<string, HttpRequestMessage> NetworkRequestStarting;
        public event Action<string, HttpResponseMessage> NetworkRequestCompleted;
        public event Action<string, Exception> NetworkRequestFailed;

        public void ResetBlockedCount()
        {
            BlockedRequestCount = 0;
            BlockedCountChanged?.Invoke(this, 0);
        }

        private readonly bool _isPrivate;
        private static int _policyBindingDiagnosticsLogged;
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

        private static string SafePartition(string origin)
        {
            return string.IsNullOrWhiteSpace(origin) ? "default" : origin.ToLowerInvariant();
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

        private static string DetermineSecFetchSite(Uri referer, Uri request)
        {
            try
            {
                if (request == null) return "none";
                if (referer == null) return "none";
                var refHost = referer.Host ?? string.Empty;
                var reqHost = request.Host ?? string.Empty;
                if (string.Equals(refHost, reqHost, StringComparison.OrdinalIgnoreCase))
                    return "same-origin";
                if (IsSameSite(refHost, reqHost))
                    return "same-site";
                return "cross-site";
            }
            catch { return "none"; }
        }

        private static bool IsSameSite(string hostA, string hostB)
        {
            if (string.IsNullOrEmpty(hostA) || string.IsNullOrEmpty(hostB)) return false;
            if (hostA.EndsWith("." + hostB, StringComparison.OrdinalIgnoreCase)) return true;
            if (hostB.EndsWith("." + hostA, StringComparison.OrdinalIgnoreCase)) return true;
            var siteA = GetApproximateSiteKey(hostA);
            var siteB = GetApproximateSiteKey(hostB);
            return !string.IsNullOrEmpty(siteA) &&
                   !string.IsNullOrEmpty(siteB) &&
                   string.Equals(siteA, siteB, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetApproximateSiteKey(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            var trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (System.Net.IPAddress.TryParse(trimmed, out _))
            {
                return trimmed;
            }

            var labels = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length < 2)
            {
                return trimmed;
            }

            return labels[labels.Length - 2] + "." + labels[labels.Length - 1];
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

            ReferrerPolicyDirective? parsed = null;
            foreach (var headerValue in values)
            {
                if (string.IsNullOrWhiteSpace(headerValue))
                {
                    continue;
                }

                var tokens = headerValue.Split(',');
                foreach (var tokenGroup in tokens)
                {
                    var tokenParts = tokenGroup.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawToken in tokenParts)
                    {
                        var token = rawToken.Trim().ToLowerInvariant();
                        switch (token)
                        {
                            case "no-referrer":
                                parsed = ReferrerPolicyDirective.NoReferrer;
                                break;
                            case "no-referrer-when-downgrade":
                                parsed = ReferrerPolicyDirective.NoReferrerWhenDowngrade;
                                break;
                            case "same-origin":
                                parsed = ReferrerPolicyDirective.SameOrigin;
                                break;
                            case "origin":
                                parsed = ReferrerPolicyDirective.Origin;
                                break;
                            case "strict-origin":
                                parsed = ReferrerPolicyDirective.StrictOrigin;
                                break;
                            case "origin-when-cross-origin":
                                parsed = ReferrerPolicyDirective.OriginWhenCrossOrigin;
                                break;
                            case "unsafe-url":
                                parsed = ReferrerPolicyDirective.UnsafeUrl;
                                break;
                            case "strict-origin-when-cross-origin":
                                parsed = ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;
                                break;
                        }
                    }
                }
            }

            return parsed ?? ReferrerPolicyDirective.StrictOriginWhenCrossOrigin;
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

            BlockedRequestCount++;
            BlockedCountChanged?.Invoke(this, BlockedRequestCount);
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

            // Some navigation responses incorrectly declare Latin-1/Windows-1252 while
            // serving UTF-8 bytes. Preserve UTF-8 in that case to avoid mojibake.
            if (ShouldPreferUtf8ForLegacyDeclaredCharset(contentTypeHeader, buffer))
            {
                try
                {
                    return Encoding.UTF8.GetString(buffer);
                }
                catch
                {
                }
            }

            return decoded;
        }

        private static bool ShouldPreferUtf8ForLegacyDeclaredCharset(string contentTypeHeader, byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0 || string.IsNullOrWhiteSpace(contentTypeHeader))
            {
                return false;
            }

            string normalized = contentTypeHeader.ToLowerInvariant();
            bool declaresLegacySingleByte =
                normalized.Contains("charset=iso-8859-1", StringComparison.Ordinal) ||
                normalized.Contains("charset=latin1", StringComparison.Ordinal) ||
                normalized.Contains("charset=latin-1", StringComparison.Ordinal) ||
                normalized.Contains("charset=windows-1252", StringComparison.Ordinal) ||
                normalized.Contains("charset=cp1252", StringComparison.Ordinal);

            if (!declaresLegacySingleByte)
            {
                return false;
            }

            return IsValidUtf8(buffer);
        }

        private static bool IsValidUtf8(byte[] bytes)
        {
            if (bytes == null)
            {
                return false;
            }

            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                strictUtf8.GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void AdoptResponseReferrerPolicy(HttpResponseMessage response, string secFetchDest)
        {
            if (!IsNavigationDestination(secFetchDest))
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
            if (url == null) return null;
            if (!IsSupportedFetchScheme(url))
            {
                EngineLogCompat.Warn($"[FetchText] Blocked unsupported scheme '{url.Scheme}' for {url}", LogCategory.Security);
                return null;
            }

            // Handle data: URI scheme (e.g., data:text/css,.picture%20%7B%20background%3A%20none%3B%20%7D)
            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataUri = url.AbsoluteUri;
                    var commaIndex = dataUri.IndexOf(',');
                    if (commaIndex > 0)
                    {
                        var dataMeta = dataUri.Substring(5, commaIndex - 5); // Skip "data:" prefix
                        var content = dataUri.Substring(commaIndex + 1);
                        
                        // Check if base64 encoded
                        bool isBase64 = dataMeta.Contains("base64", StringComparison.OrdinalIgnoreCase);
                        
                        if (isBase64)
                        {
                            // Remove base64 marker and decode
                            var cleanMeta = dataMeta.Replace(";base64", "").Replace("base64", "");
                            // Fix: URL-decode before base64 decode because data URIs can be URL-encoded
                            var decodedContent = Uri.UnescapeDataString(content);
                            var bytes = Convert.FromBase64String(decodedContent);
                            return System.Text.Encoding.UTF8.GetString(bytes);
                        }
                        else
                        {
                            // URL-decode the content
                            return Uri.UnescapeDataString(content);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchText] data URI failed: {ex.Message}");
                }
                return null;
            }

            // CSP Check
            if (ActivePolicy != null)
            {
                var dest = secFetchDest ?? ""; 
                var directive = "default-src";
                if (dest == "script") directive = "script-src";
                else if (dest == "style") directive = "style-src";
                else if (dest == "worker")
                {
                    directive = ActivePolicy.Directives.ContainsKey("worker-src") ? "worker-src" : "child-src";
                }
                else if (dest == "iframe") directive = "frame-src";
                
                if (directive != "default-src" || !string.IsNullOrEmpty(dest))
                {
                    // For fetch/xhr
                    if (string.IsNullOrEmpty(dest)) directive = "connect-src";
                }
                
                // If checking subresources
                var origin = ExtractOrigin(referer);
                if (!ActivePolicy.IsAllowed(directive, url, origin))
                {
                    System.Diagnostics.Debug.WriteLine($"[CSP] Blocked {url} ({directive})");
                    return null;
                }
            }
            
            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsFileSchemeAccessAllowed())
                {
                    EngineLogCompat.Warn($"[FetchText] Blocked file scheme load by policy: {url}", LogCategory.Security);
                    return null;
                }

                try
                {
                    return await File.ReadAllTextAsync(url.LocalPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchText] file failed: {url} {ex.Message}");
                    return null;
                }
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            LastTextResponseUri = null;

            // Direct Send via NetworkClient
            // Cache Setup
            var key = url.ToString();
            string partition = SafePartition(referer?.Host);
            
            // 1. Sharded Memory Lookup
            if (_textCache.TryGet(partition, key, out var memEntry))
            {
                LastTextResponseUri = url;
                return memEntry.Body;
            }

            string folderPath = null, filePath = null, metaPath = null;
            if (!_isPrivate && !string.IsNullOrEmpty(_cacheRoot))
            {
                try {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    {
                        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                        var hash = BitConverter.ToString(hashBytes).Replace("-", "");
                        folderPath = Path.Combine(_cacheRoot, "Text", hash.Substring(0, 2));
                        filePath = Path.Combine(folderPath, hash);
                        metaPath = filePath + ".meta";
                    }
                } catch (Exception ex) {
                    EngineLogCompat.Debug($"[Network] Text cache path hashing failed: {ex.Message}", LogCategory.Network);
                } 
            }            var refererOriginal = referer;
            Uri previousRequest = null;

            try
            {
                var _startFetch = DateTimeOffset.UtcNow;
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                var maxRedirectHops = Math.Max(1, GetResilienceSettings().MaxRedirectHops);
                while (hops < maxRedirectHops)
                {
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    AddHeaderSafe(req, "Accept", string.IsNullOrWhiteSpace(accept) ? "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7" : accept);
                    BrowserSettings.ApplyBrowserRequestHeaders(req, useMobile: false);
                    
                    AddHeaderSafe(req, "Accept-Language", "en-US,en;q=0.9");
                    AddHeaderSafe(req, "Accept-Encoding", "gzip, deflate, br");

                    var effectiveReferer = refererOriginal ?? previousRequest;
                    AddHeaderSafe(req, "Sec-Fetch-Dest", string.IsNullOrWhiteSpace(secFetchDest) ? "empty" : secFetchDest);
                    var fetchMode = DetermineFetchMode(secFetchDest);
                    AddHeaderSafe(req, "Sec-Fetch-Mode", fetchMode);
                    ApplyRefererHeader(req, effectiveReferer, current, ActiveReferrerPolicy);
                    var computedReferer = ComputeReferrerHeader(effectiveReferer, current, ActiveReferrerPolicy);
                    AddHeaderSafe(req, "Sec-Fetch-Site", DetermineSecFetchSite(computedReferer, current));
                    ApplyNavigationRequestHeaders(req, secFetchDest);
                    AttachCookies(req, refererOriginal ?? current, secFetchDest);
                    
                    var cts = new System.Threading.CancellationTokenSource();
                    try
                    {
                        int sec = ResolveTimeoutSeconds(secFetchDest);
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[Network] Failed to apply request timeout: {ex.Message}", LogCategory.Network);
                    }
                    try { resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false); }
                    catch (Exception sendEx)
                    {
                        EngineLogCompat.Error($"[Network] Request failed: {current} - {sendEx.Message}", LogCategory.Network);
                        resp = null;
                    }
                    StoreResponseCookies(resp, refererOriginal ?? current);
                    
                    if (resp != null && resp.StatusCode == System.Net.HttpStatusCode.Forbidden && resp.ReasonPhrase == "Blocked by AdBlock")
                    {
                        BlockedRequestCount++;
                        BlockedCountChanged?.Invoke(this, BlockedRequestCount);
                    }

                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler on next pass
                            current = loc;
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    EngineLogCompat.Warn($"[Network] Request failed: {url} Status={(resp != null ? (int)resp.StatusCode : 0)} Hops={hops}", LogCategory.Network);
                    return null;
                }
                var finalUri = resp?.RequestMessage?.RequestUri ?? current ?? url;
                LastTextResponseUri = finalUri;
                // NoteHsts(resp, finalUri ?? url); // Handled by HstsHandler

                var ct = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null;
                var ctHeader = resp.Content?.Headers?.ContentType?.ToString();
                
                // --- Phase 2: Encoding-aware text decoding ---
                string text = null;
                try 
                { 
                    // Read raw bytes first
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    
                    // MIME Sniff if declared type is missing or generic
                    var effectiveMime = ct;
                    if (string.IsNullOrEmpty(ct) || ct == "application/octet-stream")
                    {
                        effectiveMime = MimeSniffer.SniffMimeType(bytes, ct);
                        System.Diagnostics.Debug.WriteLine($"[ResourceLoader] MIME sniffed: {ct} -> {effectiveMime} for {url}");
                    }
                    
                    // Decode bytes to string using detected encoding
                    text = EncodingSniffer.DecodeToUtf8(bytes, ctHeader);
                    
                    var detectedEncoding = EncodingSniffer.DetermineEncoding(bytes, ctHeader);
                    System.Diagnostics.Debug.WriteLine($"[ResourceLoader] Encoding: {detectedEncoding.WebName} for {url}");
                }
                catch (Exception bodyEx)
                {
                    EngineLogCompat.Debug($"[FetchTextError] body read failed url={url} ex={bodyEx.Message}", LogCategory.Network);
                    text = null;
                }
                try { 
                    var _elapsed = DateTimeOffset.UtcNow - _startFetch; 
                    var _msg = $"[Network] GET {url} → {(int)resp.StatusCode} in {(int)_elapsed.TotalMilliseconds}ms"; 
                    EngineLogCompat.Info(_msg, LogCategory.Network);
                    if (LogSink != null) LogSink(_msg); 
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[Network] Post-request logging failed: {ex.Message}", LogCategory.Network);
                }

                if (resp != null)
                {
                    Console.WriteLine($"[FetchTextDebug] url={url} status={resp.StatusCode} type={ct} len={text?.Length ?? -1}");
                    foreach (var h in resp.Headers) Console.WriteLine($"[FetchTextHeader] {h.Key}: {string.Join(",", h.Value)}");
                }

                if (LooksTextual(ct))
                {
                    if (IsTopLevelDocumentRequest(secFetchDest))
                    {
                        FenBrowser.Core.Verification.ContentVerifier.RegisterSource(
                            url?.ToString() ?? "unknown",
                            text?.Length ?? 0,
                            text?.GetHashCode() ?? 0,
                            authoritative: true);
                    }

                    // Phase 2.3: Sharded Memory Cache
                    var entry = new TextEntry { Body = text ?? string.Empty, ContentType = ct ?? string.Empty };
                    
                    // Partition by Referer Host (or default)
                    string partitionKey = SafePartition(refererOriginal?.Host);
                    _textCache.Put(partitionKey, key, entry);

                    // disk cache (skip if private)
                    if (!_isPrivate)
                    {
                        try
                        {
                            Directory.CreateDirectory(folderPath); // Ensure dir exists
                            await File.WriteAllTextAsync(filePath, entry.Body).ConfigureAwait(false);
                            var metaPayload = DateTimeOffset.UtcNow.ToString("o") + "|" + (finalUri != null ? finalUri.AbsoluteUri : string.Empty);
                            await File.WriteAllTextAsync(metaPath, metaPayload).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            EngineLogCompat.Debug($"[Network] Disk text cache write failed for {url}: {ex.Message}", LogCategory.Network);
                        }
                    }
                }
                if (string.IsNullOrEmpty(text))
                {
                    EngineLogCompat.Debug($"[FetchTextEmpty] url={url}", LogCategory.Network);
                }
                return text;
            }
            catch (Exception ex) {
                var msg = $"[FetchTextException] url={url} ex={ex.Message}";
                EngineLogCompat.Error(msg, LogCategory.Network);
                try
                {
                    LogSink?.Invoke(msg);
                }
                catch (Exception sinkEx)
                {
                    EngineLogCompat.Debug($"[Network] FetchText exception sink failed: {sinkEx.Message}", LogCategory.Network);
                }
                // Return a clear error message for text resources
                return $"<!-- Resource load failed: {System.Net.WebUtility.HtmlEncode(url?.ToString() ?? "(null)")} : {System.Net.WebUtility.HtmlEncode(ex.Message)} -->";
            }
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

            // Handle data: URI scheme (e.g., data:text/html,<div>Hello</div>)
            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataUri = url.OriginalString;
                    var commaIndex = dataUri.IndexOf(',');
                    if (commaIndex > 5)
                    {
                        var dataMeta = dataUri.Substring(5, commaIndex - 5); // Skip "data:" prefix
                        var content = dataUri.Substring(commaIndex + 1);
                        
                        // Determine content type from meta
                        string contentType = "text/plain";
                        if (dataMeta.Contains("text/html")) contentType = "text/html";
                        else if (dataMeta.Contains("text/css")) contentType = "text/css";
                        else if (dataMeta.Contains("application/javascript")) contentType = "application/javascript";
                        
                        // Check if base64 encoded
                        bool isBase64 = dataMeta.Contains("base64", StringComparison.OrdinalIgnoreCase);
                        string decodedContent;
                        
                        if (isBase64)
                        {
                            var cleanContent = Uri.UnescapeDataString(content);
                            var bytes = Convert.FromBase64String(cleanContent);
                            decodedContent = System.Text.Encoding.UTF8.GetString(bytes);
                        }
                        else
                        {
                            decodedContent = Uri.UnescapeDataString(content);
                        }
                        
                        return new FetchResult { Status = FetchStatus.Success, Content = decodedContent, FinalUri = url, ContentType = contentType };
                    }
                    return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Invalid data URI format", FinalUri = url };
                }
                catch (Exception ex)
                {
                    return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = $"Data URI parsing failed: {ex.Message}", FinalUri = url };
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
            var cacheKey = url.ToString();
            var cachePartition = SafePartition(referer?.Host);
            if (_textCache.TryGet(cachePartition, cacheKey, out var cachedDetailed))
            {
                LastTextResponseUri = url;
                return new FetchResult
                {
                    Status = FetchStatus.Success,
                    Content = cachedDetailed.Body,
                    FinalUri = url,
                    ContentType = cachedDetailed.ContentType
                };
            }

            var refererOriginal = referer;
            var topLevelDocumentUri = context.TopLevelDocumentUri ??
                context.FrameDocumentUri ??
                refererOriginal ??
                url;
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
                        ActiveReferrerPolicy,
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
                // NoteHsts(resp, finalUri ?? url); // Handled by HstsHandler

                var ct = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null;
                
                if (!resp.IsSuccessStatusCode)
                {
                    // 404, 500, etc.
                    string errBody = null;
                    try { errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        EngineLogCompat.Debug($"[Network] Failed to read error body for {finalUri}: {ex.Message}", LogCategory.Network);
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
                try
                {
                    bodyBytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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

                var maxTextBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxTextBodyBytes);
                if (bodyBytes.Length > maxTextBodyBytes)
                {
                    EngineLogCompat.Warn(
                        $"[Network.Resilience] Text body limit exceeded for '{finalUri}' ({bodyBytes.Length} > {maxTextBodyBytes}).",
                        LogCategory.Network);
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
                        InputSizeBytes = bodyBytes.Length,
                        IsRetryable = false
                    };
                }

                if (ShouldBlockCorb(
                    corbFetchMode,
                    secFetchDest,
                    refererOriginal,
                    finalUri,
                    resp,
                    bodyBytes.AsSpan(0, Math.Min(bodyBytes.Length, 512)),
                    out var corbReason))
                {
                    return new FetchResult
                    {
                        Status = FetchStatus.UnknownError,
                        ErrorDetail = corbReason,
                        StatusCode = (int)resp.StatusCode,
                        FinalUri = finalUri,
                        ContentType = ct,
                        Headers = resp.Headers,
                        Redirected = redirectChain.Count > 1,
                        RedirectCount = Math.Max(0, redirectChain.Count - 1),
                        RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                        FailureReason = FetchFailureReasonCode.CorbBlocked,
                        IsRetryable = false
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

                // Parse X-Frame-Options header for frame-embedding enforcement
                var xFramePolicy = XFrameOptionsPolicy.None;
                string xFrameAllowFrom = null;
                if (resp.Headers.TryGetValues("X-Frame-Options", out var xfoValues))
                {
                    var xfoRaw = string.Join(",", xfoValues).Trim().ToUpperInvariant();
                    if (xfoRaw.Contains("DENY"))
                        xFramePolicy = XFrameOptionsPolicy.Deny;
                    else if (xfoRaw.Contains("SAMEORIGIN"))
                        xFramePolicy = XFrameOptionsPolicy.SameOrigin;
                    else if (xfoRaw.Contains("ALLOW-FROM"))
                    {
                        xFramePolicy = XFrameOptionsPolicy.AllowFrom;
                        // Extract the URI after "ALLOW-FROM "
                        var raw = string.Join(",", xfoValues).Trim();
                        var idx = raw.IndexOf("ALLOW-FROM", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) xFrameAllowFrom = raw.Substring(idx + "ALLOW-FROM".Length).Trim();
                    }
                }

                var referrerPolicy = ParseReferrerPolicy(resp);
                AdoptResponseReferrerPolicy(resp, secFetchDest);

                if (string.Equals(secFetchDest, "iframe", StringComparison.OrdinalIgnoreCase) &&
                    !IsFrameEmbeddingAllowed(xFramePolicy, xFrameAllowFrom, refererOriginal, finalUri))
                {
                    BlockedRequestCount++;
                    BlockedCountChanged?.Invoke(this, BlockedRequestCount);
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
                if (!IsTopLevelDocumentRequest(secFetchDest))
                {
                    try
                    {
                        _textCache.Put(cachePartition, cacheKey,
                            new TextEntry { Body = text ?? string.Empty, ContentType = effectiveMime ?? string.Empty });
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
                    Redirected = redirectChain.Count > 1,
                    RedirectCount = Math.Max(0, redirectChain.Count - 1),
                    RedirectChain = redirectChain.Select(u => u.AbsoluteUri).ToArray(),
                    XFrameOptions = xFramePolicy,
                    XFrameAllowFromUri = xFrameAllowFrom,
                    ReferrerPolicy = referrerPolicy,
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
                AddHeaderSafe(req, "Sec-Fetch-Site", DetermineSecFetchSite(computedReferer, url));
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
                AdoptResponseReferrerPolicy(resp, secFetchDest);
                LastTextResponseUri = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : url;
                string text = null;
                try
                {
                    text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
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
                BlockedRequestCount++;
                BlockedCountChanged?.Invoke(this, BlockedRequestCount);
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

            // Handle data URIs
            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataUri = url.OriginalString;
                    var commaIndex = dataUri.IndexOf(',');
                    if (commaIndex > 5)
                    {
                        var header = dataUri.Substring(5, commaIndex - 5);
                        var data = dataUri.Substring(commaIndex + 1);
                        
                        bool isBase64 = header.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0;
                        byte[] bytes;
                        
                        if (isBase64)
                        {
                            try 
                            { 
                                // Fix: URL-decode before base64 decode
                                var decodedData = Uri.UnescapeDataString(data);
                                bytes = Convert.FromBase64String(decodedData); 
                            }
                            catch (Exception ex)
                            {
                                EngineLogCompat.Debug($"[FetchImage] Invalid base64 data URI for {url}: {ex.Message}", LogCategory.Network);
                                return null;
                            }
                        }
                        else
                        {
                            var decoded = Uri.UnescapeDataString(data);
                            bytes = Encoding.UTF8.GetBytes(decoded);
                        }
                        
                        return new MemoryStream(bytes);
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    EngineLogCompat.Debug($"[FetchImage] Data URI decode failed for {url}: {ex.Message}", LogCategory.Network);
                    return null;
                }
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            var key = url.AbsoluteUri;

            // Sharded Lookup
            string partition = SafePartition(topLevelDocumentUri?.Host);
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
                        ActiveReferrerPolicy,
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

                var buf = await HttpCache.Instance.GetBufferAsync(null, req).ConfigureAwait(false) ?? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var maxImageBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxImageBodyBytes);
                if (buf.Length > maxImageBodyBytes)
                {
                    EngineLogCompat.Warn(
                        $"[Network.Resilience] Dropping image '{url}' because body size {buf.Length} exceeded limit {maxImageBodyBytes}.",
                        LogCategory.Network);
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
                string partitionKey = SafePartition(topLevelDocumentUri?.Host);
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
            if (string.Equals(secFetchDest, "image", StringComparison.OrdinalIgnoreCase) &&
                referer != null &&
                referer.IsAbsoluteUri &&
                url.IsAbsoluteUri &&
                string.Equals(referer.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                BlockedRequestCount++;
                BlockedCountChanged?.Invoke(this, BlockedRequestCount);
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
                        ActiveReferrerPolicy,
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

                byte[] buf;
                try
                {
                    buf = resp.IsSuccessStatusCode
                        ? await HttpCache.Instance.GetBufferAsync(null, req).ConfigureAwait(false) ?? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
                        : await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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
                var maxBodyBytes = Math.Max(64 * 1024, GetResilienceSettings().MaxImageBodyBytes);
                if (buf.Length > maxBodyBytes)
                {
                    EngineLogCompat.Warn(
                        $"[Network.Resilience] Dropping binary response '{url}' because body size {buf.Length} exceeded limit {maxBodyBytes}.",
                        LogCategory.Network);
                    return BinaryFailure(
                        BinaryFetchFailureReason.BodySizeLimitExceeded,
                        current,
                        $"Body size {buf.Length} exceeded limit {maxBodyBytes}",
                        redirectChain,
                        (int)resp.StatusCode,
                        resp.Content?.Headers?.ContentType?.MediaType,
                        bodySizeAllowed: false);
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
                ActiveReferrerPolicy,
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
            preflight.Headers.TryAddWithoutValidation("Sec-Fetch-Site", DetermineSecFetchSite(originUri, request.RequestUri));
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
            // Generate a tracking ID
            string id = Guid.NewGuid().ToString("N");
            
            try
            {
                /* [PERF-REMOVED] */
                NetworkRequestStarting?.Invoke(id, req);
                
                var resp = await _client.SendAsync(req, token).ConfigureAwait(false);
                
                /* [PERF-REMOVED] */
                try
                {
                    NetworkRequestCompleted?.Invoke(id, resp);
                }
                catch (Exception) 
                {
                    /* [PERF-REMOVED] */
                }
                return resp;
            }
            catch (Exception ex)
            {
                /* [PERF-REMOVED] */
                NetworkRequestFailed?.Invoke(id, ex);
                throw;
            }
        }
        public async Task<string> FetchCssAsync(Uri url)
        {
            if (url == null) return null;
            // Use FetchTextDetailedAsync to inspect headers before returning
            var result = await FetchTextDetailedAsync(url, 
                accept: "text/css,*/*;q=0.1", 
                secFetchDest: "style");

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
            if (result.Headers != null && result.Headers.TryGetValues("X-Content-Type-Options", out var xctoVals))
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
    }
}


