using System;
using System.Collections.Generic;
using System.Linq;
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
        private static readonly HashSet<string> SafelistedMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "HEAD", "POST"
        };

        private static readonly HashSet<string> SafelistedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept", "Accept-Language", "Content-Language", "Content-Type"
        };

        private static readonly HashSet<string> BrowserManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "User-Agent",
            "Sec-Fetch-Dest",
            "Sec-Fetch-Mode",
            "Sec-Fetch-Site",
            "Sec-Fetch-User",
            "Sec-CH-UA",
            "Sec-CH-UA-Mobile",
            "Sec-CH-UA-Platform",
            "Sec-CH-UA-Platform-Version",
            "Sec-CH-UA-Full-Version",
            "Sec-CH-UA-Full-Version-List",
            "Sec-CH-UA-Arch",
            "Sec-CH-UA-Bitness",
            "Sec-CH-UA-Model",
            "DNT"
        };

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

        public static string SerializeOrigin(Uri originUri)
        {
            if (originUri == null || !originUri.IsAbsoluteUri)
            {
                return null;
            }

            var origin = $"{originUri.Scheme}://{originUri.Host}";
            if (!originUri.IsDefaultPort && originUri.Port != -1)
            {
                origin += $":{originUri.Port}";
            }

            return origin;
        }

        public static bool TryGetOriginUri(HttpRequestMessage request, out Uri originUri)
        {
            originUri = null;
            if (request == null)
            {
                return false;
            }

            if (request.Headers.TryGetValues("Origin", out var originValues))
            {
                foreach (var originValue in originValues)
                {
                    if (string.Equals(originValue, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (Uri.TryCreate(originValue, UriKind.Absolute, out originUri))
                    {
                        return true;
                    }
                }
            }

            if (request.Headers.Referrer != null)
            {
                originUri = BuildOriginUri(request.Headers.Referrer);
                return originUri != null;
            }

            return false;
        }

        public static bool RequiresPreflight(HttpRequestMessage request, Uri originUri)
        {
            if (request?.RequestUri == null || originUri == null || IsSameOrigin(request.RequestUri, originUri))
            {
                return false;
            }

            if (!SafelistedMethods.Contains(request.Method.Method))
            {
                return true;
            }

            return GetCorsUnsafeRequestHeaderNames(request).Count > 0;
        }

        public static IReadOnlyList<string> GetCorsUnsafeRequestHeaderNames(HttpRequestMessage request)
        {
            var headerNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (request == null)
            {
                return headerNames.ToArray();
            }

            foreach (var header in request.Headers)
            {
                if (IsUnsafeRequestHeader(header.Key, header.Value))
                {
                    headerNames.Add(header.Key.ToLowerInvariant());
                }
            }

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    if (IsUnsafeRequestHeader(header.Key, header.Value))
                    {
                        headerNames.Add(header.Key.ToLowerInvariant());
                    }
                }
            }

            return headerNames.ToArray();
        }

        public static bool IsPreflightAllowed(HttpResponseMessage response, HttpRequestMessage request, Uri originUri)
        {
            if (response == null || request?.RequestUri == null || originUri == null)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (!IsCorsAllowed(response, request.RequestUri, originUri))
            {
                return false;
            }

            if (!HeaderAllowsToken(response, "Access-Control-Allow-Methods", request.Method.Method))
            {
                return false;
            }

            var requestedHeaders = GetCorsUnsafeRequestHeaderNames(request);
            if (requestedHeaders.Count == 0)
            {
                return true;
            }

            return requestedHeaders.All(header => HeaderAllowsToken(response, "Access-Control-Allow-Headers", header));
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

        private static Uri BuildOriginUri(Uri candidate)
        {
            if (candidate == null || !candidate.IsAbsoluteUri)
            {
                return null;
            }

            try
            {
                return new UriBuilder(candidate.Scheme, candidate.Host, candidate.IsDefaultPort ? -1 : candidate.Port).Uri;
            }
            catch
            {
                return null;
            }
        }

        private static bool HeaderAllowsToken(HttpResponseMessage response, string headerName, string token)
        {
            if (!response.Headers.TryGetValues(headerName, out var values))
            {
                return false;
            }

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (var part in value.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed == "*")
                    {
                        return true;
                    }

                    if (string.Equals(trimmed, token, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsUnsafeRequestHeader(string name, IEnumerable<string> values)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase) ||
                BrowserManagedHeaders.Contains(name))
            {
                return false;
            }

            if (!SafelistedHeaders.Contains(name))
            {
                return true;
            }

            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                var mediaType = values?.FirstOrDefault();
                return !IsSafelistedContentType(mediaType);
            }

            return false;
        }

        private static bool IsSafelistedContentType(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var normalized = value.Split(';', 2)[0].Trim();
            return string.Equals(normalized, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "text/plain", StringComparison.OrdinalIgnoreCase);
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
