using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private const int MaxCacheEntries = 2048;
        private const int DnsTypeA = 1;
        private const int DnsTypeCname = 5;
        private const int DnsTypeAaaa = 28;

        private sealed class CacheEntry
        {
            public IPAddress[] Addresses { get; set; }
            public DateTimeOffset ExpiresAt { get; set; }
        }

        private readonly record struct DohAnswer(string Name, int Type, string Data, int TtlSeconds);

        private static readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly HttpClient _dohClient = CreateClient();
        private static readonly IdnMapping _idn = new();
        private static int _cacheTrimInProgress;

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

            var trimmedHost = host.Trim().TrimEnd('.');
            if (IPAddress.TryParse(trimmedHost, out var ipLiteral))
            {
                return new[] { ipLiteral };
            }

            var normalizedHost = NormalizeDnsHost(trimmedHost);
            if (string.IsNullOrEmpty(normalizedHost))
            {
                return Array.Empty<IPAddress>();
            }

            // localhost., foo.localhost, and mDNS .local names are local namespace
            // inputs and must not be sent to an external DoH provider.
            if (IsLocalHost(normalizedHost))
            {
                return Array.Empty<IPAddress>();
            }

            var endpoint = GetEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return Array.Empty<IPAddress>();
            }

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
                TrimCacheIfNeeded();
                return addresses;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                EngineLogCompat.Warn(
                    $"[SecureDNS] DoH resolution failed for host (type={ex.GetType().Name}).",
                    LogCategory.Network);
                return Array.Empty<IPAddress>();
            }
        }

        private static void TrimCacheIfNeeded()
        {
            if (_cache.Count <= MaxCacheEntries ||
                Interlocked.CompareExchange(ref _cacheTrimInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var pair in _cache)
                {
                    if (pair.Value.ExpiresAt <= now)
                    {
                        _cache.TryRemove(pair.Key, out _);
                    }
                }

                var excess = _cache.Count - MaxCacheEntries;
                if (excess <= 0)
                    return;

                // Prefer entries closest to expiry. ConcurrentDictionary enumeration is
                // a safe point-in-time view; removals are best-effort and another thread
                // may already have replaced an entry.
                foreach (var pair in _cache
                    .OrderBy(static pair => pair.Value.ExpiresAt)
                    .Take(excess))
                {
                    _cache.TryRemove(pair.Key, out _);
                }
            }
            finally
            {
                Volatile.Write(ref _cacheTrimInProgress, 0);
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
                    $"[SecureDNS] DoH {type} lookup failed (type={ex.GetType().Name}).",
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
            if (root.TryGetProperty("Status", out var statusElement) &&
                (!statusElement.TryGetInt32(out var status) || status != 0))
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            var parsedAnswers = new List<DohAnswer>();
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("name", out var nameElement) ||
                    !answer.TryGetProperty("type", out var typeElement) ||
                    !answer.TryGetProperty("data", out var dataElement) ||
                    !typeElement.TryGetInt32(out var recordType))
                {
                    continue;
                }

                var recordName = NormalizeDnsHost(nameElement.GetString());
                var data = dataElement.GetString();
                if (string.IsNullOrEmpty(recordName) || string.IsNullOrWhiteSpace(data))
                {
                    continue;
                }

                var ttl = 300;
                if (answer.TryGetProperty("TTL", out var ttlElement) && ttlElement.TryGetInt32(out var parsedTtl))
                {
                    ttl = Math.Max(0, parsedTtl);
                }

                parsedAnswers.Add(new DohAnswer(recordName, recordType, data.Trim(), ttl));
            }

            if (parsedAnswers.Count == 0)
            {
                return new List<(IPAddress address, int ttlSeconds)>();
            }

            // Only trust address records whose owner is the queried name or is reachable
            // from it through CNAME records in this same authenticated DoH response.
            // This prevents an unrelated A/AAAA record injected into Answer from being
            // accepted merely because its data field parses as an IP address.
            var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { host };
            bool changed;
            int remainingPasses = parsedAnswers.Count;
            do
            {
                changed = false;
                foreach (var answer in parsedAnswers)
                {
                    if (answer.Type != DnsTypeCname || !allowedNames.Contains(answer.Name))
                        continue;

                    var canonicalTarget = NormalizeDnsHost(answer.Data);
                    if (!string.IsNullOrEmpty(canonicalTarget) && allowedNames.Add(canonicalTarget))
                    {
                        changed = true;
                    }
                }
            }
            while (changed && --remainingPasses > 0);

            int expectedType = string.Equals(type, "AAAA", StringComparison.Ordinal)
                ? DnsTypeAaaa
                : DnsTypeA;
            var expectedFamily = expectedType == DnsTypeAaaa
                ? AddressFamily.InterNetworkV6
                : AddressFamily.InterNetwork;

            var result = new List<(IPAddress address, int ttlSeconds)>();
            var seenAddresses = new HashSet<IPAddress>();
            foreach (var answer in parsedAnswers)
            {
                if (answer.Type != expectedType ||
                    !allowedNames.Contains(answer.Name) ||
                    !IPAddress.TryParse(answer.Data, out var ip) ||
                    ip.AddressFamily != expectedFamily ||
                    !seenAddresses.Add(ip))
                {
                    continue;
                }

                result.Add((ip, answer.TtlSeconds));
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

        private static string NormalizeDnsHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            var normalized = host.Trim().TrimEnd('.');
            if (normalized.Length == 0)
            {
                return null;
            }

            try
            {
                normalized = _idn.GetAscii(normalized);
            }
            catch (ArgumentException)
            {
                return null;
            }

            normalized = normalized.ToLowerInvariant();
            if (normalized.Length > 253)
            {
                return null;
            }

            var labels = normalized.Split('.', StringSplitOptions.None);
            foreach (var label in labels)
            {
                if (label.Length == 0 || label.Length > 63)
                {
                    return null;
                }
            }

            return normalized;
        }

        private static bool IsLocalHost(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }
    }
}
