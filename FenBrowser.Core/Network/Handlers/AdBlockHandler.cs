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
            // Basic hardcoded list for demonstration. 
            // In a real app, this would load from a file/EasyList.
            _blockedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "doubleclick.net",
                "googleadservices.com",
                "googlesyndication.com",
                "adservice.google.com",
                "facebook.com/tr",
                "analytics.google.com"
            };
            _isEnabled = isEnabled ?? (() => true);
        }

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            var uri = context.Request.RequestUri;
            if (_isEnabled() && uri != null && _blockedDomains.Contains(uri.Host))
            {
                context.IsBlocked = true;
                context.BlockReason = "AdBlock";
                context.Response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    ReasonPhrase = "Blocked by AdBlock"
                };
                return; // Short-circuit
            }

            await next();
        }
    }
}
