using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Lightweight runtime URL safety gate for known-dangerous hosts.
    /// This is a local deny-list guard, not a full remote Safe Browsing feed.
    /// </summary>
    public sealed class SafeBrowsingHandler : INetworkHandler
    {
        private readonly Func<bool> _isEnabled;

        private static readonly HashSet<string> DangerousHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "testsafebrowsing.appspot.com",
            "malware.testing.google.test",
            "phishing.testing.google.test"
        };

        public SafeBrowsingHandler(Func<bool> isEnabled)
        {
            _isEnabled = isEnabled ?? (() => false);
        }

        public Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            if (context?.Request?.RequestUri == null || !_isEnabled())
            {
                return next();
            }

            var uri = context.Request.RequestUri;
            var host = uri.Host ?? string.Empty;

            if (DangerousHosts.Contains(host))
            {
                context.IsBlocked = true;
                context.BlockReason = "SafeBrowsing";
                context.Response = new HttpResponseMessage((HttpStatusCode)451)
                {
                    ReasonPhrase = "Blocked by Safe Browsing"
                };
                return Task.CompletedTask;
            }

            return next();
        }
    }
}
