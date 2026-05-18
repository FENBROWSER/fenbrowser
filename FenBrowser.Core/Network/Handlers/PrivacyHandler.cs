using System;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    public class PrivacyHandler : INetworkHandler
    {
        private static readonly StringComparison HostComparison = StringComparison.OrdinalIgnoreCase;

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            var req = context.Request;
            var settings = BrowserSettings.Instance;

            // 1. DNT Header
            if (settings.SendDoNotTrack)
            {
                if (!req.Headers.Contains("DNT"))
                {
                    req.Headers.TryAddWithoutValidation("DNT", "1");
                }
            }
            else
            {
                // Explicitly remove DNT when user opts out.
                req.Headers.Remove("DNT");
            }

            // 2. Referer Trimming
            // If Referer is present and cross-origin, trim to origin only.
            if (req.Headers.Referrer != null && req.RequestUri != null)
            {
                if (!IsSameOrigin(req.Headers.Referrer, req.RequestUri))
                {
                    // Cross-origin: strip path/query
                    var trimmed = new Uri(req.Headers.Referrer.GetLeftPart(UriPartial.Authority));
                    req.Headers.Referrer = trimmed;
                }
            }

            // 3. Third-Party Cookie Blocking
            if (settings.BlockThirdPartyCookies)
            {
                // Check if Sec-Fetch-Site suggests cross-site/cross-origin
                // Note: The ResourceManager sets up Sec-Fetch-Site headers *after* this handler usually, in FetchTextWithOptionsAsync, 
                // but this handler runs in the pipeline. We might need to rely on host comparison.
                
                // We'll trust Sec-Fetch-Site if present, otherwise compare manually.
                bool isThirdParty = false;
                if (req.Headers.TryGetValues("Sec-Fetch-Site", out var values))
                {
                    foreach (var raw in values)
                    {
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            continue;
                        }

                        var parts = raw.Split(',');
                        foreach (var part in parts)
                        {
                            var token = part.Trim();
                            if (token.Equals("cross-site", StringComparison.OrdinalIgnoreCase) ||
                                token.Equals("cross-origin", StringComparison.OrdinalIgnoreCase))
                            {
                                isThirdParty = true;
                                break;
                            }
                        }

                        if (isThirdParty)
                        {
                            break;
                        }
                    }
                }
                else if (req.Headers.Referrer != null && req.RequestUri != null)
                {
                    isThirdParty = !IsSameSite(req.Headers.Referrer, req.RequestUri);
                }

                if (isThirdParty)
                {
                    req.Headers.Remove("Cookie");
                }
            }

            await next();
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Host, right.Host, HostComparison)
                && left.Port == right.Port;
        }

        // Heuristic site comparison without PSL dependency:
        // equal hosts, sibling subdomains under the same registrable suffix, or exact IP match.
        private static bool IsSameSite(Uri left, Uri right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            var leftHost = left.Host;
            var rightHost = right.Host;
            if (string.IsNullOrWhiteSpace(leftHost) || string.IsNullOrWhiteSpace(rightHost))
            {
                return false;
            }

            if (string.Equals(leftHost, rightHost, HostComparison))
            {
                return true;
            }

            var leftHostType = Uri.CheckHostName(leftHost);
            var rightHostType = Uri.CheckHostName(rightHost);
            if (leftHostType == UriHostNameType.IPv4 || leftHostType == UriHostNameType.IPv6 ||
                rightHostType == UriHostNameType.IPv4 || rightHostType == UriHostNameType.IPv6)
            {
                return false;
            }

            return IsSubdomainOrSame(leftHost, rightHost) || IsSubdomainOrSame(rightHost, leftHost);
        }

        private static bool IsSubdomainOrSame(string host, string root)
        {
            return host.Length > root.Length
                && host.EndsWith(root, HostComparison)
                && host[host.Length - root.Length - 1] == '.';
        }
    }
}
