using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Enhanced Tracking Prevention (ETP) handler.
    /// Blocks known trackers, tracking pixels, and third-party tracking resources.
    /// </summary>
    public sealed class TrackingPreventionHandler : INetworkHandler
    {
        // Known tracking domains (subset of EasyPrivacy/Disconnect lists)
        private static readonly HashSet<string> _trackerDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Major Ad/Tracking Networks
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
            
            // Twitter/X
            "analytics.twitter.com",
            "ads-twitter.com",
            
            // Microsoft
            "bat.bing.com",
            "clarity.ms",
            
            // Other common trackers
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

        // Known tracking pixel patterns
        private static readonly string[] _trackingPixelPatterns = new[]
        {
            "/pixel", "/tracking", "/beacon", "/collect", "/log", "/impression",
            "/1x1", "/blank.gif", "/spacer.gif", "/pixel.gif", "/t.gif", "/p.gif"
        };

        private static readonly HashSet<string> _commonCountryCodeSecondLevelDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ac", "co", "com", "edu", "gov", "net", "org"
        };

        /// <summary>Whether Enhanced Tracking Prevention is enabled.</summary>
        public static bool IsEnabled { get; set; } = true;

        /// <summary>Count of blocked tracking requests in current session.</summary>
        public static int BlockedCount { get; private set; }

        /// <summary>Reset the blocked count (call on new navigation).</summary>
        public static void ResetBlockedCount() => BlockedCount = 0;

        /// <summary>Check if a URL should be blocked as a tracker.</summary>
        public static bool IsTracker(Uri uri, Uri pageOrigin = null)
        {
            if (uri == null || !IsEnabled) return false;

            // Never block first-party requests for the active page, including same-site CDNs.
            if (pageOrigin != null && (IsSameOrigin(uri, pageOrigin) || IsSameSite(uri.Host, pageOrigin.Host)))
            {
                return false;
            }

            var host = uri.Host?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(host))
            {
                if (_trackerDomains.Contains(host)) return true;
                
                foreach (var tracker in _trackerDomains)
                {
                    if (host.EndsWith("." + tracker, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
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
                   string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
        }

        private static bool IsSameSite(string hostA, string hostB)
        {
            if (string.IsNullOrWhiteSpace(hostA) || string.IsNullOrWhiteSpace(hostB))
            {
                return false;
            }

            var normalizedA = NormalizeHost(hostA);
            var normalizedB = NormalizeHost(hostB);
            if (string.IsNullOrEmpty(normalizedA) || string.IsNullOrEmpty(normalizedB))
            {
                return false;
            }

            if (string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var siteKeyA = GetSiteKey(normalizedA);
            var siteKeyB = GetSiteKey(normalizedB);
            return string.Equals(siteKeyA, siteKeyB, StringComparison.OrdinalIgnoreCase);
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

        private static string GetSiteKey(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return string.Empty;
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                Uri.CheckHostName(host) == UriHostNameType.IPv4 ||
                Uri.CheckHostName(host) == UriHostNameType.IPv6)
            {
                return host;
            }

            var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length <= 2)
            {
                return host;
            }

            var registrableLabelCount = 2;
            var topLevelLabel = labels[^1];
            var secondLevelLabel = labels[^2];
            if (topLevelLabel.Length == 2 &&
                labels.Length >= 3 &&
                _commonCountryCodeSecondLevelDomains.Contains(secondLevelLabel))
            {
                registrableLabelCount = 3;
            }

            return string.Join(".", labels, labels.Length - registrableLabelCount, registrableLabelCount);
        }

        // INetworkHandler implementation
        public Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            if (context?.Request?.RequestUri == null || !IsEnabled)
            {
                return next();
            }

            var pageOrigin = context.Request.Headers.Referrer;
            if (IsTracker(context.Request.RequestUri, pageOrigin))
            {
                // Block the request
                BlockedCount++;
                Console.WriteLine($"[ETP] Blocked: {context.Request.RequestUri.Host} ({context.Request.RequestUri.AbsolutePath})");
                
                context.IsBlocked = true;
                context.BlockReason = "Blocked by Enhanced Tracking Prevention";
                context.Response = new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
                {
                    ReasonPhrase = "Blocked by ETP"
                };
                
                return Task.CompletedTask; // Don't call next, we're blocking
            }

            return next();
        }
    }
}
