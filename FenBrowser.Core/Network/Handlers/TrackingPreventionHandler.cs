using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Security;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Enhanced Tracking Prevention (ETP) handler.
    /// Blocks known trackers, tracking pixels, and third-party tracking resources.
    /// </summary>
    public sealed class TrackingPreventionHandler : INetworkHandler
    {
        private static readonly HashSet<string> _trackerDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "doubleclick.net",
            "googlesyndication.com",
            "googleadservices.com",
            "google-analytics.com",
            "googletagmanager.com",
            "googletagservices.com",
            "facebook.net",
            "connect.facebook.net",
            "analytics.facebook.com",
            "pixel.facebook.com",
            "ad.doubleclick.net",
            "stats.g.doubleclick.net",
            "analytics.twitter.com",
            "ads-twitter.com",
            "bat.bing.com",
            "clarity.ms",
            "scorecardresearch.com",
            "quantserve.com",
            "taboola.com",
            "outbrain.com",
            "criteo.com",
            "criteo.net",
            "adsrvr.org",
            "adnxs.com",
            "rubiconproject.com",
            "pubmatic.com",
            "casalemedia.com",
            "bluekai.com",
            "demdex.net",
            "krxd.net",
            "mixpanel.com",
            "amplitude.com",
            "segment.io",
            "segment.com",
            "hotjar.com",
            "fullstory.com",
            "mouseflow.com",
            "crazyegg.com",
            "optimizely.com",
            "newrelic.com",
            "nr-data.net"
        };

        private static readonly string[] _trackingPixelPatterns = new[]
        {
            "/pixel", "/tracking", "/beacon", "/collect", "/log", "/impression",
            "/1x1", "/blank.gif", "/spacer.gif", "/pixel.gif", "/t.gif", "/p.gif"
        };

        private static int _blockedCount;

        public static bool IsEnabled { get; set; } = true;

        public static int BlockedCount => Volatile.Read(ref _blockedCount);

        public static void ResetBlockedCount() => Interlocked.Exchange(ref _blockedCount, 0);

        public static bool IsTracker(Uri uri, Uri pageOrigin = null)
        {
            if (uri == null || !IsEnabled) return false;

            if (pageOrigin != null && (IsSameOrigin(uri, pageOrigin) || SiteIdentityService.Default.CompareSameSite(uri, pageOrigin)))
            {
                return false;
            }

            var host = NormalizeHost(uri.IdnHost);
            if (!string.IsNullOrEmpty(host))
            {
                if (_trackerDomains.Contains(host)) return true;

                foreach (var tracker in _trackerDomains)
                {
                    if (host.EndsWith("." + tracker, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Generic path names such as /log, /collect, and /beacon are common on
            // perfectly legitimate first-party endpoints. They are only useful as a
            // tracking heuristic when we actually know this request is cross-site.
            // With no page context, fail open for the heuristic instead of blocking a
            // top-level/first-party request based on its path alone.
            if (pageOrigin == null)
            {
                return false;
            }

            var path = uri.AbsolutePath?.ToLowerInvariant() ?? "";
            foreach (var pattern in _trackingPixelPatterns)
            {
                if (MatchesTrackingPixelPattern(path, pattern)) return true;
            }

            return false;
        }

        private static bool IsSameOrigin(Uri a, Uri b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeHost(a.IdnHost), NormalizeHost(b.IdnHost), StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
        }

        private static bool MatchesTrackingPixelPattern(string path, string pattern)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pattern))
            {
                return false;
            }

            var searchIndex = 0;
            while (searchIndex < path.Length)
            {
                var matchIndex = path.IndexOf(pattern, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    return false;
                }

                var endIndex = matchIndex + pattern.Length;
                if (endIndex >= path.Length)
                {
                    return true;
                }

                var nextChar = path[endIndex];
                if (!char.IsLetterOrDigit(nextChar))
                {
                    return true;
                }

                searchIndex = matchIndex + 1;
            }

            return false;
        }

        private static string NormalizeHost(string host)
        {
            return host?.Trim().TrimEnd('.').ToLowerInvariant();
        }

        public Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            if (context?.Request?.RequestUri == null || !IsEnabled)
            {
                return next();
            }

            // Normal browser fetches carry user-agent-owned Fetch Metadata. Do not
            // classify a top-level navigation (site=none) or same-site request as a
            // third-party tracker merely because its destination host appears in the
            // built-in tracker list. This also removes Referer as the authority on the
            // normal ResourceManager path.
            var fetchSite = TryGetFetchSite(context.Request);
            if (fetchSite is "none" or "same-origin" or "same-site")
            {
                return next();
            }

            // Requests created outside the normal browser header policy may not have
            // Fetch Metadata. Preserve the legacy referrer-based fallback for those
            // internal/embedder callers; cross-site browser requests use the same
            // origin only as additional same-site protection inside IsTracker.
            var pageOrigin = context.Request.Headers.Referrer;
            if (IsTracker(context.Request.RequestUri, pageOrigin))
            {
                Interlocked.Increment(ref _blockedCount);

                context.IsBlocked = true;
                context.BlockReason = "Blocked by Enhanced Tracking Prevention";
                context.Response = new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
                {
                    ReasonPhrase = "Blocked by ETP"
                };

                return Task.CompletedTask;
            }

            return next();
        }

        private static string TryGetFetchSite(HttpRequestMessage request)
        {
            if (request?.Headers == null || !request.Headers.TryGetValues("Sec-Fetch-Site", out var values))
            {
                return null;
            }

            string parsed = null;
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var item in tokens)
                {
                    var token = item.Trim().ToLowerInvariant();
                    if (token is not ("none" or "same-origin" or "same-site" or "cross-site"))
                    {
                        continue;
                    }

                    if (parsed != null && !string.Equals(parsed, token, StringComparison.Ordinal))
                    {
                        // Conflicting Fetch Metadata is malformed. Do not let an
                        // ambiguous value become a same-site bypass; fall back to the
                        // conservative legacy classification below.
                        return null;
                    }

                    parsed = token;
                }
            }

            return parsed;
        }
    }
}
