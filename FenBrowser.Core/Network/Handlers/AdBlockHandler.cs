using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    public class AdBlockHandler : INetworkHandler
    {
        private readonly HashSet<string> _blockedDomains;
        private readonly Func<bool> _isEnabled;

        public AdBlockHandler(Func<bool> isEnabled = null)
        {
            // Small built-in compatibility/privacy list. Domain matching is label-aware
            // so a rule for example.com also covers sub.example.com but never
            // notexample.com. Path-specific rules are handled separately below.
            _blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "doubleclick.net",
                "googleadservices.com",
                "googlesyndication.com",
                "adservice.google.com",
                "analytics.google.com"
            };
            _isEnabled = isEnabled ?? (() => true);
        }

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            if (context?.Request?.RequestUri is not { } uri || !_isEnabled())
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (ShouldBlock(uri))
            {
                context.IsBlocked = true;
                context.BlockReason = "AdBlock";
                context.Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "Blocked by AdBlock"
                };
                return;
            }

            await next().ConfigureAwait(false);
        }

        private bool ShouldBlock(Uri uri)
        {
            var host = NormalizeHost(uri);
            foreach (var blockedDomain in _blockedDomains)
            {
                if (HostMatches(host, blockedDomain))
                {
                    return true;
                }
            }

            // The Facebook tracking-pixel endpoint is path-specific. Keeping it out of
            // the domain set avoids the previous dead "facebook.com/tr" host rule and
            // avoids blocking unrelated facebook.com resources.
            return HostMatches(host, "facebook.com") &&
                   (string.Equals(uri.AbsolutePath, "/tr", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.StartsWith("/tr/", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeHost(Uri uri)
        {
            if (uri == null)
            {
                return string.Empty;
            }

            return (uri.IdnHost ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        }

        private static bool HostMatches(string host, string blockedDomain)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(blockedDomain))
            {
                return false;
            }

            var normalizedHost = host.Trim().TrimEnd('.');
            var normalizedDomain = blockedDomain.Trim().TrimStart('.').TrimEnd('.');
            if (normalizedHost.Length == 0 || normalizedDomain.Length == 0)
            {
                return false;
            }

            return string.Equals(normalizedHost, normalizedDomain, StringComparison.OrdinalIgnoreCase) ||
                   normalizedHost.EndsWith("." + normalizedDomain, StringComparison.OrdinalIgnoreCase);
        }
    }
}
