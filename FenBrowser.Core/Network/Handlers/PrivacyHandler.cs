using System;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    public class PrivacyHandler : INetworkHandler
    {
        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var req = context.Request;
            if (req == null)
            {
                await next().ConfigureAwait(false);
                return;
            }

            var settings = BrowserSettings.Instance;

            // DNT is browser-controlled state. Always remove a stale/caller-supplied
            // value before applying the current browser preference so reused or cloned
            // requests cannot override the user's setting with DNT: 0/invalid values.
            req.Headers.Remove("DNT");
            if (settings.SendDoNotTrack)
            {
                req.Headers.TryAddWithoutValidation("DNT", "1");
            }

            // Referrer-Policy is applied centrally by BrowserRequestHeaderPolicy.
            // Do not apply a second unconditional cross-origin trimming policy here;
            // doing so changes the semantics of valid policies such as unsafe-url.

            if (settings.BlockThirdPartyCookies && IsThirdPartyRequest(context))
            {
                req.Headers.Remove("Cookie");
            }

            await next().ConfigureAwait(false);
        }

        private static bool IsThirdPartyRequest(NetworkContext context)
        {
            var request = context.Request;
            var requestUri = request?.RequestUri;
            if (requestUri == null)
            {
                return false;
            }

            var fetchContext = context.FetchContext;
            if (fetchContext != null)
            {
                if (fetchContext.IsTopLevelNavigation)
                {
                    return false;
                }

                var topLevelUri = fetchContext.TopLevelDocumentUri
                    ?? fetchContext.FrameDocumentUri
                    ?? fetchContext.InitiatorUri;

                if (topLevelUri != null)
                {
                    return string.Equals(
                        BrowserRequestHeaderPolicy.DetermineSite(topLevelUri, requestUri),
                        "cross-site",
                        StringComparison.Ordinal);
                }
            }

            // Compatibility path for internal/embedder requests that bypass the normal
            // BrowserRequestHeaderPolicy and therefore have no typed FetchContext.
            if (request.Headers.TryGetValues("Sec-Fetch-Site", out var values))
            {
                foreach (var raw in values)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.Trim().Equals("cross-site", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            return request.Headers.Referrer != null &&
                   string.Equals(
                       BrowserRequestHeaderPolicy.DetermineSite(request.Headers.Referrer, requestUri),
                       "cross-site",
                       StringComparison.Ordinal);
        }
    }
}
