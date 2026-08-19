using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    /// <summary>
    /// Handles Cross-Origin Resource Sharing (CORS) enforcement helpers.
    /// </summary>
    public sealed class CorsHandler : INetworkHandler
    {
        public const string AuthorRequestHeadersOptionKey = "FenBrowser.Cors.AuthorRequestHeaders";
        public const string CredentialsModeOptionKey = "FenBrowser.Cors.CredentialsMode";
        private const int CorsSafelistedValueMaxBytes = 128;
        private const int CorsSafelistAggregateMaxBytes = 1024;

        private static readonly HttpRequestOptionsKey<string[]> s_authorRequestHeadersKey =
            new(AuthorRequestHeadersOptionKey);
        private static readonly HttpRequestOptionsKey<string> s_credentialsModeKey =
            new(CredentialsModeOptionKey);
        private static readonly HttpRequestOptionsKey<string[]> s_unsafeRequestHeadersKey =
            new("FenBrowser.Cors.UnsafeRequestHeaders");

        private static readonly HashSet<string> SafelistedMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "HEAD", "POST"
        };

        private static readonly HashSet<string> SafelistedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Accept", "Accept-Language", "Content-Language", "Content-Type", "Range"
        };

        private static readonly HashSet<string> BrowserManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "User-Agent",
            "Accept-Encoding",
            "Accept-Charset",
            "Priority",
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

        private static readonly HashSet<string> ForbiddenOrSyntheticHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Access-Control-Request-Headers",
            "Access-Control-Request-Method",
            "Connection",
            "Content-Length",
            "Cookie",
            "Cookie2",
            "Date",
            "Expect",
            "Host",
            "Keep-Alive",
            "Origin",
            "Referer",
            "Set-Cookie",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
            "Via"
        };

        public static bool IsCorsAllowed(HttpResponseMessage response, Uri requestUri, Uri originUri)
        {
            return IsCorsAllowed(response, requestUri, originUri, GetCredentialsMode(response?.RequestMessage));
        }

        private static bool IsCorsAllowed(
            HttpResponseMessage response,
            Uri requestUri,
            Uri originUri,
            string credentialsMode)
        {
            if (response == null || requestUri == null)
                return false;

            if (originUri != null && IsSameOrigin(requestUri, originUri))
                return true;
            if (originUri == null)
                return false;

            if (!TryGetSingleHeaderValue(response, "Access-Control-Allow-Origin", out var allowedOrigin))
                return false;

            var includeCredentials = IsIncludeCredentialsMode(credentialsMode);
            if (string.Equals(allowedOrigin, "*", StringComparison.Ordinal))
                return !includeCredentials;

            var serializedOrigin = SerializeOrigin(originUri);
            if (serializedOrigin == null || !string.Equals(allowedOrigin, serializedOrigin, StringComparison.Ordinal))
                return false;

            if (!includeCredentials)
                return true;

            return TryGetSingleHeaderValue(response, "Access-Control-Allow-Credentials", out var allowCredentials) &&
                   string.Equals(allowCredentials, "true", StringComparison.Ordinal);
        }

        public static string SerializeOrigin(Uri originUri)
        {
            if (originUri == null || !originUri.IsAbsoluteUri || string.IsNullOrEmpty(originUri.Host))
                return null;

            try
            {
                var builder = new UriBuilder(
                    originUri.Scheme,
                    originUri.Host,
                    originUri.IsDefaultPort ? -1 : originUri.Port);
                return builder.Uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                return null;
            }
        }

        public static bool TryGetOriginUri(HttpRequestMessage request, out Uri originUri)
        {
            originUri = null;
            if (request == null)
                return false;

            if (request.Headers.TryGetValues("Origin", out var originValues))
            {
                foreach (var originValue in originValues)
                {
                    // Opaque origins serialize as "null" and cannot be represented
                    // faithfully by System.Uri. Callers must not reinterpret them as
                    // a missing/same-origin tuple.
                    if (string.Equals(originValue, "null", StringComparison.Ordinal))
                        return false;

                    if (Uri.TryCreate(originValue, UriKind.Absolute, out var candidate))
                    {
                        originUri = BuildOriginUri(candidate);
                        return originUri != null;
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
                return false;

            if (!SafelistedMethods.Contains(request.Method.Method))
                return true;

            return GetCorsUnsafeRequestHeaderNames(request).Count > 0;
        }

        public static IReadOnlyList<string> GetCorsUnsafeRequestHeaderNames(HttpRequestMessage request)
        {
            if (request == null)
                return Array.Empty<string>();
            if (request.Options.TryGetValue(s_unsafeRequestHeadersKey, out var cached))
                return cached;

            var unsafeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var potentiallyUnsafeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var safelistValueSize = 0;

            if (request.Options.TryGetValue(s_authorRequestHeadersKey, out var authorHeaders) && authorHeaders != null)
            {
                foreach (var headerName in authorHeaders)
                {
                    if (string.IsNullOrWhiteSpace(headerName))
                        continue;

                    if (TryGetHeaderValues(request, headerName, out var values))
                    {
                        ClassifyRequestHeader(
                            headerName,
                            values,
                            unsafeNames,
                            potentiallyUnsafeNames,
                            ref safelistValueSize);
                    }
                }
            }
            else
            {
                foreach (var header in request.Headers)
                {
                    ClassifyRequestHeader(
                        header.Key,
                        header.Value,
                        unsafeNames,
                        potentiallyUnsafeNames,
                        ref safelistValueSize);
                }

                if (request.Content != null)
                {
                    foreach (var header in request.Content.Headers)
                    {
                        ClassifyRequestHeader(
                            header.Key,
                            header.Value,
                            unsafeNames,
                            potentiallyUnsafeNames,
                            ref safelistValueSize);
                    }
                }
            }

            // Fetch's CORS-unsafe request-header names algorithm revokes the
            // safelist for all potentially-safe names when their aggregate value
            // size exceeds 1024 bytes.
            if (safelistValueSize > CorsSafelistAggregateMaxBytes)
            {
                foreach (var name in potentiallyUnsafeNames)
                    unsafeNames.Add(name);
            }

            var result = unsafeNames.Select(name => name.ToLowerInvariant()).ToArray();
            Array.Sort(result, StringComparer.Ordinal);
            request.Options.Set(s_unsafeRequestHeadersKey, result);
            return result;
        }

        public static bool IsPreflightAllowed(HttpResponseMessage response, HttpRequestMessage request, Uri originUri)
        {
            if (response == null || request?.RequestUri == null || originUri == null)
                return false;
            if (!response.IsSuccessStatusCode)
                return false;

            var credentialsMode = GetCredentialsMode(request);
            if (!IsCorsAllowed(response, request.RequestUri, originUri, credentialsMode))
                return false;

            var wildcardAllowed = !IsIncludeCredentialsMode(credentialsMode);
            if (!HeaderAllowsToken(
                    response,
                    "Access-Control-Allow-Methods",
                    request.Method.Method,
                    wildcardAllowed,
                    isRequestHeaderName: false))
            {
                return false;
            }

            var requestedHeaders = GetCorsUnsafeRequestHeaderNames(request);
            if (requestedHeaders.Count == 0)
                return true;

            return requestedHeaders.All(header =>
                HeaderAllowsToken(
                    response,
                    "Access-Control-Allow-Headers",
                    header,
                    wildcardAllowed,
                    isRequestHeaderName: true));
        }

        public static bool IsSameOrigin(Uri a, Uri b)
        {
            if (a == null || b == null)
                return false;
            if (!string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase))
                return false;

            var portA = a.IsDefaultPort ? GetDefaultPort(a.Scheme) : a.Port;
            var portB = b.IsDefaultPort ? GetDefaultPort(b.Scheme) : b.Port;
            return portA == portB;
        }

        public static void SetCredentialsMode(HttpRequestMessage request, string credentialsMode)
        {
            if (request != null)
                request.Options.Set(s_credentialsModeKey, NormalizeCredentialsMode(credentialsMode));
        }

        public static string GetCredentialsMode(HttpRequestMessage request)
        {
            if (request != null && request.Options.TryGetValue(s_credentialsModeKey, out var credentialsMode))
                return NormalizeCredentialsMode(credentialsMode);

            return "same-origin";
        }

        private static string NormalizeCredentialsMode(string credentialsMode)
        {
            var normalized = (credentialsMode ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "omit" => "omit",
                "include" => "include",
                _ => "same-origin"
            };
        }

        private static bool IsIncludeCredentialsMode(string credentialsMode) =>
            string.Equals(credentialsMode, "include", StringComparison.Ordinal);

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
            if (candidate == null || !candidate.IsAbsoluteUri || string.IsNullOrEmpty(candidate.Host))
                return null;

            try
            {
                return new UriBuilder(
                    candidate.Scheme,
                    candidate.Host,
                    candidate.IsDefaultPort ? -1 : candidate.Port).Uri;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetSingleHeaderValue(HttpResponseMessage response, string headerName, out string value)
        {
            value = null;
            if (response?.Headers == null || !response.Headers.TryGetValues(headerName, out var values))
                return false;

            foreach (var rawValue in values)
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                    continue;

                var trimmed = rawValue.Trim();
                if (trimmed.IndexOf(',') >= 0 || value != null)
                {
                    value = null;
                    return false;
                }

                value = trimmed;
            }

            return value != null;
        }

        private static bool HeaderAllowsToken(
            HttpResponseMessage response,
            string headerName,
            string token,
            bool wildcardAllowed,
            bool isRequestHeaderName)
        {
            if (!response.Headers.TryGetValues(headerName, out var values))
                return false;

            // Authorization is a CORS non-wildcard request-header name. ACAH: *
            // never authorizes it; it must be listed explicitly.
            var tokenCanUseWildcard = wildcardAllowed &&
                !(isRequestHeaderName && string.Equals(token, "Authorization", StringComparison.OrdinalIgnoreCase));

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                foreach (var part in value.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed == "*" && tokenCanUseWildcard)
                        return true;

                    if (string.Equals(trimmed, token, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static void ClassifyRequestHeader(
            string name,
            IEnumerable<string> values,
            ISet<string> unsafeNames,
            ISet<string> potentiallyUnsafeNames,
            ref int safelistValueSize)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (IsBrowserControlledOrForbidden(name))
                return;

            if (IsCorsSafelistedRequestHeader(name, values, out var valueByteLength))
            {
                potentiallyUnsafeNames.Add(name);
                safelistValueSize = SaturatingAdd(safelistValueSize, valueByteLength);
            }
            else
            {
                unsafeNames.Add(name);
            }
        }

        private static bool IsCorsSafelistedRequestHeader(
            string name,
            IEnumerable<string> values,
            out int valueByteLength)
        {
            valueByteLength = 0;
            if (!SafelistedHeaders.Contains(name))
                return false;

            var value = JoinHeaderValues(values);
            valueByteLength = Encoding.UTF8.GetByteCount(value);
            if (valueByteLength > CorsSafelistedValueMaxBytes)
                return false;

            if (string.Equals(name, "Accept", StringComparison.OrdinalIgnoreCase))
                return !ContainsCorsUnsafeRequestHeaderByte(value);

            if (string.Equals(name, "Accept-Language", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Content-Language", StringComparison.OrdinalIgnoreCase))
            {
                return ContainsOnlySafelistedLanguageBytes(value);
            }

            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                return !ContainsCorsUnsafeRequestHeaderByte(value) && IsSafelistedContentType(value);
            }

            if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase))
                return IsSafelistedSingleRange(value);

            return false;
        }

        private static bool IsBrowserControlledOrForbidden(string name)
        {
            return ForbiddenOrSyntheticHeaders.Contains(name) ||
                   BrowserManagedHeaders.Contains(name) ||
                   name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetHeaderValues(
            HttpRequestMessage request,
            string headerName,
            out IEnumerable<string> values)
        {
            if (request.Headers.TryGetValues(headerName, out var requestValues))
            {
                values = requestValues;
                return true;
            }

            if (request.Content != null && request.Content.Headers.TryGetValues(headerName, out var contentValues))
            {
                values = contentValues;
                return true;
            }

            values = Array.Empty<string>();
            return false;
        }

        private static string JoinHeaderValues(IEnumerable<string> values)
        {
            return values == null ? string.Empty : string.Join(", ", values);
        }

        private static bool ContainsCorsUnsafeRequestHeaderByte(string value)
        {
            foreach (var c in value)
            {
                if ((c < 0x20 && c != '\t') || c == 0x7F)
                    return true;

                switch (c)
                {
                    case '"':
                    case '(':
                    case ')':
                    case ':':
                    case '<':
                    case '>':
                    case '?':
                    case '@':
                    case '[':
                    case '\\':
                    case ']':
                    case '{':
                    case '}':
                        return true;
                }
            }

            return false;
        }

        private static bool ContainsOnlySafelistedLanguageBytes(string value)
        {
            foreach (var c in value)
            {
                var allowed =
                    (c >= '0' && c <= '9') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    c == ' ' || c == '*' || c == ',' || c == '-' ||
                    c == '.' || c == ';' || c == '=';

                if (!allowed)
                    return false;
            }

            return true;
        }

        private static bool IsSafelistedSingleRange(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var range = value.AsSpan(6);
            if (range.IsEmpty || range[0] == '-')
                return false; // suffix ranges are not CORS-safelisted

            var dash = range.IndexOf('-');
            if (dash <= 0 || range[(dash + 1)..].IndexOf('-') >= 0 || range.IndexOf(',') >= 0)
                return false;

            if (!TryParseUnsignedDecimal(range[..dash], out var start))
                return false;

            var endSpan = range[(dash + 1)..];
            if (endSpan.IsEmpty)
                return true;

            return TryParseUnsignedDecimal(endSpan, out var end) && start <= end;
        }

        private static bool TryParseUnsignedDecimal(ReadOnlySpan<char> value, out ulong result)
        {
            result = 0;
            if (value.IsEmpty)
                return false;

            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                    return false;

                var digit = (uint)(c - '0');
                if (result > (ulong.MaxValue - digit) / 10)
                    return false;
                result = result * 10 + digit;
            }

            return true;
        }

        private static int SaturatingAdd(int current, int addition)
        {
            if (addition <= 0)
                return current;
            if (current >= CorsSafelistAggregateMaxBytes + 1)
                return current;
            if (addition > CorsSafelistAggregateMaxBytes + 1 - current)
                return CorsSafelistAggregateMaxBytes + 1;
            return current + addition;
        }

        public static void SetAuthorRequestHeaders(HttpRequestMessage request, IEnumerable<string> headerNames)
        {
            if (request == null)
                return;

            if (headerNames == null)
            {
                request.Options.Set(s_authorRequestHeadersKey, Array.Empty<string>());
                return;
            }

            var unique = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var headerName in headerNames)
            {
                if (!string.IsNullOrWhiteSpace(headerName))
                    unique.Add(headerName.Trim());
            }

            request.Options.Set(s_authorRequestHeadersKey, unique.ToArray());
        }

        private static bool IsSafelistedContentType(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            var normalized = value.Split(';', 2)[0].Trim();
            return string.Equals(normalized, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "text/plain", StringComparison.OrdinalIgnoreCase);
        }

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            // CORS response filtering is performed by the canonical fetch caller today.
            // Keep this handler pass-through until that stage owns the complete request
            // origin (including opaque origins) and response-filtering context.
            await next().ConfigureAwait(false);
        }
    }
}
