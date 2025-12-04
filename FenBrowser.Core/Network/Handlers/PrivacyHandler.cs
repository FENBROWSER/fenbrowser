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

            await next();
        }
    }
}
