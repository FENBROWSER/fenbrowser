using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private const int MaxDohResponseBytes = 64 * 1024;

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
            ct.ThrowIfCancellationRequested();

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

            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            var normalizedHost = host.Trim().ToLowerInvariant();
            var cacheKey = endpoint + "\n" + normalizedHost;
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    return cached.Address;
                }

                _cache.TryRemove(cacheKey, out _);
            }

            try
            {
                var records = await QueryRecordsAsync(endpoint, normalizedHost, "A", ct).ConfigureAwait(false);
                if (records.Count == 0)
                {
                    records = await QueryRecordsAsync(endpoint, normalizedHost, "AAAA", ct).ConfigureAwait(false);
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
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

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
