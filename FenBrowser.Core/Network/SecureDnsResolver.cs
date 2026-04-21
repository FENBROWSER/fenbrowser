using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
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
        private sealed class CacheEntry
        {
            public IPAddress Address { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient _dohClient = CreateClient();

        public static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            if (IPAddress.TryParse(host, out var ipLiteral))
            {
                return ipLiteral;
            }

            if (IsLocalHost(host))
            {
                return null;
            }

            var cacheKey = host.Trim().ToLowerInvariant();
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Address;
            }

            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            try
            {
                var records = await QueryRecordsAsync(endpoint, cacheKey, "A", ct).ConfigureAwait(false);
                if (records.Count == 0)
                {
                    records = await QueryRecordsAsync(endpoint, cacheKey, "AAAA", ct).ConfigureAwait(false);
                }

                if (records.Count == 0)
                {
                    return null;
                }

                var selected = records[0];
                var ttl = Math.Max(30, Math.Min(3600, selected.ttlSeconds));
                _cache[cacheKey] = new CacheEntry
                {
                    Address = selected.address,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl)
                };
                return selected.address;
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn($"[SecureDNS] DoH resolution failed for '{host}': {ex.Message}", LogCategory.Network);
                return null;
            }
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

            using var response = await _dohClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
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

                var ttl = 300;
                if (answer.TryGetProperty("TTL", out var ttlElement) && ttlElement.TryGetInt32(out var parsedTtl))
                {
                    ttl = parsedTtl;
                }

                result.Add((ip, ttl));
            }

            return result;
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
            var fromEnv = Environment.GetEnvironmentVariable("FEN_SECURE_DNS_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.Trim();
            }

            var fromSettings = BrowserSettings.Instance.SecureDnsEndpoint;
            if (!string.IsNullOrWhiteSpace(fromSettings))
            {
                return fromSettings.Trim();
            }

            return "https://cloudflare-dns.com/dns-query";
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
