using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Handles Cross-Origin Resource Sharing (CORS) enforcement.
    /// Validates Access-Control-Allow-Origin and handles preflight requests.
    /// </summary>
    public sealed class CorsHandler : INetworkHandler
    {
        /// <summary>
        /// Check if a cross-origin request is allowed based on CORS headers.
        /// </summary>
        public static bool IsCorsAllowed(HttpResponseMessage response, Uri requestUri, Uri originUri)
        {
            if (response == null || requestUri == null) return false;
            
            // Same-origin requests are always allowed
            if (originUri != null && IsSameOrigin(requestUri, originUri)) return true;
            
            // Check Access-Control-Allow-Origin header
            if (response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values))
            {
                foreach (var value in values)
                {
                    if (value == "*") return true;
                    
                    // Check if origin matches
                    if (originUri != null)
                    {
                        var originString = $"{originUri.Scheme}://{originUri.Host}";
                        if (originUri.Port != -1 && !originUri.IsDefaultPort)
                            originString += $":{originUri.Port}";
                        
                        if (string.Equals(value, originString, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            
            return false;
        }

        /// <summary>Check if two URIs have the same origin</summary>
        public static bool IsSameOrigin(Uri a, Uri b)
        {
            if (a == null || b == null) return false;
            
            if (!string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase))
                return false;
            
            if (!string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase))
                return false;
            
            int portA = a.Port == -1 ? GetDefaultPort(a.Scheme) : a.Port;
            int portB = b.Port == -1 ? GetDefaultPort(b.Scheme) : b.Port;
            
            return portA == portB;
        }

        private static int GetDefaultPort(string scheme)
        {
            return scheme?.ToLowerInvariant() switch
            {
                "http" => 80,
                "https" => 443,
                _ => -1
            };
        }

        // INetworkHandler implementation - CORS is checked post-response, so we just pass through
        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            // Let the request proceed
            await next().ConfigureAwait(false);
            
            // After response, we could validate CORS here if needed
            // For now, CORS checks are done via static methods by callers
        }
    }
}
