using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network.Filtering;
using FenBrowser.Core.Security;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Enhanced Tracking Prevention (ETP) handler.
    /// Blocks known trackers, tracking pixels, and third-party tracking resources.
    /// </summary>
    public sealed class TrackingPreventionHandler : INetworkHandler
    {
        private static readonly SignedNetworkRuleSetStore RuleSets = new(new NetworkRuleSet(
            version: 1,
            generatedAtUtc: DateTimeOffset.UnixEpoch,
            expiresAtUtc: null,
            rules: Array.ConvertAll(new[]
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
        }, static domain => new NetworkFilterRule(domain))));

        private static int _blockedCount;

        public static bool IsEnabled { get; set; } = true;

        public static int BlockedCount => Volatile.Read(ref _blockedCount);

        public static void ResetBlockedCount() => Interlocked.Exchange(ref _blockedCount, 0);

        public static long RuleSetVersion => RuleSets.Current.Version;

        public static bool TryInstallSignedRuleSet(ReadOnlySpan<byte> ruleset, ReadOnlySpan<byte> signature, ECDsa verifier, DateTimeOffset now, out string error)
            => RuleSets.TryInstall(ruleset, signature, verifier, now, out error);

        public static bool TryRollbackRuleSet() => RuleSets.TryRollback();

        public static bool IsTracker(Uri uri, Uri pageOrigin = null)
        {
            if (uri == null || !IsEnabled) return false;

            if (pageOrigin != null && (IsSameOrigin(uri, pageOrigin) || SiteIdentityService.Default.CompareSameSite(uri, pageOrigin)))
            {
                return false;
            }

            return RuleSets.Current.Matches(uri);
        }

        private static bool IsSameOrigin(Uri a, Uri b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(NormalizeHost(a.IdnHost), NormalizeHost(b.IdnHost), StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
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
            var destination = TryGetSingleHeader(context.Request, "Sec-Fetch-Dest");
            if (IsTracker(context.Request.RequestUri, pageOrigin) && RuleSets.Current.Matches(context.Request.RequestUri, destination))
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

        private static string TryGetSingleHeader(HttpRequestMessage request, string name)
        {
            if (!request.Headers.TryGetValues(name, out var values)) return null;
            using var enumerator = values.GetEnumerator();
            return enumerator.MoveNext() ? enumerator.Current?.Trim().ToLowerInvariant() : null;
        }
    }
}
