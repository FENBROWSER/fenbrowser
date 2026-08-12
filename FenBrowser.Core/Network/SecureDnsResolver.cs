using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Best-effort DNS-over-HTTPS resolver used when Secure DNS is enabled.
    /// </summary>
    internal static class SecureDnsResolver
    {
        private const int MaxDohResponseBytes = 64 * 1024;

        private sealed class CacheEntry
        {
            public IPAddress[] Addresses { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient _dohClient = CreateClient();

        public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
        {
            var addresses = await ResolveAllAsync(host, ct).ConfigureAwait(false);
            return addresses.Count > 0 ? addresses[0] : null;
        }

        public static async Task<IReadOnlyList<IPAddress>> ResolveAllAsync(string host, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(host))
            {
                return Array.Empty<IPAddress>();
            }

            if (IPAddress.TryParse(host, out var ipLiteral))
            {
                return new[] { ipLiteral };
            }

            if (IsLocalHost(host))
            {
                return Array.Empty<IPAddress>();
            }

            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return Array.Empty<IPAddress>();
            }

            var normalizedHost = host.Trim().ToLowerInvariant();
            var cacheKey = endpoint + "\n" + normalizedHost;
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Addresses;
                }

                _cache.TryRemove(cacheKey, out _);
            }

            try
            {
                // Query both address families in parallel. The old A-first/AAAA-only-on-
                // failure path structurally prevented dual-stack connection racing.
                var ipv4Task = QueryRecordsBestEffortAsync(endpoint, normalizedHost, "A", ct);
                var ipv6Task = QueryRecordsBestEffortAsync(endpoint, normalizedHost, "AAAA", ct);
                await Task.WhenAll(ipv4Task, ipv6Task).ConfigureAwait(false);

                var records = InterleaveAddressFamilies(ipv6Task.Result, ipv4Task.Result);
                if (records.Count == 0)
                {
                    return Array.Empty<IPAddress>();
                }

                var addresses = new IPAddress[records.Count];
                var minTtl = 3600;
                for (var i = 0; i < records.Count; i++)
                {
                    addresses[i] = records[i].address;
                    minTtl = Math.Min(minTtl, records[i].ttlSeconds);
                }

                var ttl = Math.Max(30, Math.Min(3600, minTtl));
                _cache[cacheKey] = new CacheEntry
                {
                    Addresses = addresses,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl)
                };
                return addresses;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                EngineLogCompat.Warn($"[SecureDNS] DoH resolution failed for '{host}': {ex.Message}", LogCategory.Network);
                return Array.Empty<IPAddress>();
            }
        }

        private static async Task<List<(IPAddress address, int ttlSeconds)>> QueryRecordsBestEffortAsync(
            string endpoint,
            string host,
            string type,
            CancellationToken ct)
        {
            try
            {
                return await QueryRecordsAsync(endpoint, host, type, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One address family failing must not discard a usable answer from
                // the other family. The aggregate resolver still fails only when both
                // families produce no addresses.
                EngineLogCompat.Debug(
                    $"[SecureDNS] DoH {type} lookup failed for '{host}': {ex.Message}",
                    LogCategory.Network);
                return new List<(IPAddress address, int ttlSeconds)>();
            }
        }

        private static List<(IPAddress address, int ttlSeconds)> InterleaveAddressFamilies(
            IReadOnlyList<(IPAddress address, int ttlSeconds)> ipv6,
            IReadOnlyList<(IPAddress address, int ttlSeconds)> ipv4)
        {
            var result = new List<(IPAddress address, int ttlSeconds)>(ipv6.Count + ipv4.Count);
            var seen = new HashSet<IPAddress>();
            var max = Math.Max(ipv6.Count, ipv4.Count);

            for (var i = 0; i < max; i++)
            {
                if (i < ipv6.Count && seen.Add(ipv6[i].address))
                {
                    result.Add(ipv6[i]);
                }

                if (i < ipv4.Count && seen.Add(ipv4[i].address))
                {
                    result.Add(ipv4[i]);
                }
            }

            return result;
        }

        private static async Task<List<(IPAddress address, int ttlSeconds)>> QueryRecordsAsync(
            string endpoint,
            string host,
            string type,
            CancellationToken ct)
        {
            var separator = endpoint.Contains("?", StringComparison.Ordinal) ? "&" : "?";
            var url = $"{endpoint}{separator}name={Uri.EscapeDataString(host)}&type={type}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/dns-json");

            using var response = await _dohClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            if (response.Content.Headers.ContentLength is long declaredLength &&
                declaredLength > MaxDohResponseBytes)
            {
                throw new InvalidDataException(
                    $"DoH response exceeded the {MaxDohResponseBytes}-byte limit.");
            }

            var body = await ReadBoundedResponseAsync(response.Content, ct).ConfigureAwait(false);
            if (body.Length == 0)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("Status", out var statusElement) && statusElement.GetInt32() != 0)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            var result = new List<(IPAddress address, int ttlSeconds)>();
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("data", out var dataElement))
                {
                    continue;
                }

                var data = dataElement.GetString();
                if (string.IsNullOrWhiteSpace(data) || !IPAddress.TryParse(data, out var ip))
                {
                    continue;
                }

                // Ignore an address of the wrong family if a DoH endpoint returns a
                // mixed answer section. CNAME records are already filtered by IP parse.
                if (string.Equals(type, "A", StringComparison.Ordinal) && ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }
                if (string.Equals(type, "AAAA", StringComparison.Ordinal) && ip.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    continue;
                }

                var ttl = 300;
                if (answer.TryGetProperty("TTL", out var ttlElement) && ttlElement.TryGetInt32(out var parsedTtl))
                {
                    ttl = parsedTtl;
                }

                result.Add((ip, ttl));
            }

            return result;
        }

        private static async Task<byte[]> ReadBoundedResponseAsync(
            HttpContent content,
            CancellationToken ct)
        {
            await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream(capacity: Math.Min(MaxDohResponseBytes, 4096));
            var chunk = new byte[4096];

            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxDohResponseBytes)
                {
                    throw new InvalidDataException(
                        $"DoH response exceeded the {MaxDohResponseBytes}-byte limit.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        private static HttpClient CreateClient()
        {
            var config = NetworkConfiguration.Instance;
            var handler = new HttpClientHandler
            {
                UseProxy = config.UseSystemProxy,
                AutomaticDecompression = config.GetDecompressionMethods()
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, config.ConnectionTimeoutSeconds))
            };
            try
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent));
            }
            catch
            {
            }
            return client;
        }

        private static string GetEndpoint()
        {
            var configured = Environment.GetEnvironmentVariable("FEN_SECURE_DNS_ENDPOINT");
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = BrowserSettings.Instance.SecureDnsEndpoint;
            }

            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = "https://cloudflare-dns.com/dns-query";
            }

            configured = configured.Trim();
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) ||
                !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(endpoint.Host) ||
                !string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
            {
                EngineLogCompat.Warn(
                    "[SecureDNS] Ignoring invalid DoH endpoint. Secure DNS requires an absolute HTTPS URL without user-info or a fragment.",
                    LogCategory.Network);
                return null;
            }

            return endpoint.AbsoluteUri;
        }

        private static bool IsLocalHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (host.EndsWith(".local", true, CultureInfo.InvariantCulture))
            {
                return true;
            }

            return false;
        }
    }
}
