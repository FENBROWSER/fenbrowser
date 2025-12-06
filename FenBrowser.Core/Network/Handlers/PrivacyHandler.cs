using System;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    public class PrivacyHandler : INetworkHandler
    {
        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            var req = context.Request;

            // 1. DNT Header
            if (!req.Headers.Contains("DNT"))
            {
                req.Headers.TryAddWithoutValidation("DNT", "1");
            }

            // 2. Referer Trimming
            // If Referer is present and cross-origin, trim to origin only.
            if (req.Headers.Referrer != null && req.RequestUri != null)
            {
                var refHost = req.Headers.Referrer.Host;
                var reqHost = req.RequestUri.Host;

                if (!string.Equals(refHost, reqHost, StringComparison.OrdinalIgnoreCase))
                {
                    // Cross-origin: strip path/query
                    var trimmed = new Uri(req.Headers.Referrer.GetLeftPart(UriPartial.Authority));
                    req.Headers.Referrer = trimmed;
                }
            }

            // 3. Third-Party Cookie Blocking
            if (BrowserSettings.Instance.BlockThirdPartyCookies)
            {
                // Check if Sec-Fetch-Site suggests cross-site/cross-origin
                // Note: The ResourceManager sets up Sec-Fetch-Site headers *after* this handler usually, in FetchTextWithOptionsAsync, 
                // but this handler runs in the pipeline. We might need to rely on host comparison.
                
                // We'll trust Sec-Fetch-Site if present, otherwise compare manually.
                bool isThirdParty = false;
                if (req.Headers.TryGetValues("Sec-Fetch-Site", out var values))
                {
                    foreach(var v in values) { if (v == "cross-site") isThirdParty = true; }
                }
                else if (req.Headers.Referrer != null)
                {
                    var refHost = req.Headers.Referrer.Host;
                    var reqHost = req.RequestUri.Host;
                    // Simple domain check (not effective TLD compliant but sufficient for now)
                    if (!refHost.EndsWith(reqHost, StringComparison.OrdinalIgnoreCase) && 
                        !reqHost.EndsWith(refHost, StringComparison.OrdinalIgnoreCase))
                    {
                        isThirdParty = true;
                    }
                }

                if (isThirdParty)
                {
                    req.Headers.Remove("Cookie");
                }
            }

            await next();
        }
    }
}
