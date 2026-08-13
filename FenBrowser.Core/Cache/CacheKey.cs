using System;

namespace FenBrowser.Core.Cache
{
    /// <summary>
    /// A double-key for cache entries, consisting of the Top-Level Partition (e.g., origin) and the Resource URL.
    /// This prevents cross-site tracking via cache timing attacks.
    /// </summary>
    public readonly struct CacheKey : IEquatable<CacheKey>
    {
        public readonly string PartitionKey;
        public readonly string Url;

        public CacheKey(string partitionKey, string url)
        {
            PartitionKey = NormalizePartitionKey(partitionKey);
            Url = NormalizeUrl(url);
        }

        public bool IsEmpty => Url.Length == 0;

        public bool Equals(CacheKey other)
        {
            // Path and query components remain case-sensitive. Scheme/host/default
            // port normalization happens when the key is constructed.
            return string.Equals(PartitionKey, other.PartitionKey, StringComparison.Ordinal) &&
                   string.Equals(Url, other.Url, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is CacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(PartitionKey),
                StringComparer.Ordinal.GetHashCode(Url));
        }

        public static bool operator ==(CacheKey left, CacheKey right) => left.Equals(right);

        public static bool operator !=(CacheKey left, CacheKey right) => !left.Equals(right);

        public override string ToString() => $"[{PartitionKey}] {Url}";

        private static string NormalizePartitionKey(string partitionKey)
        {
            return string.IsNullOrWhiteSpace(partitionKey) ? "default" : partitionKey.Trim();
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var normalized = url.Trim();
            var fragmentIndex = normalized.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                normalized = normalized[..fragmentIndex];
            }

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) ||
                (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return normalized;
            }

            try
            {
                // System.Uri canonicalizes HTTP scheme/host casing, IDN host form,
                // dot segments and default ports without lowercasing path/query.
                var builder = new UriBuilder(parsed)
                {
                    Scheme = parsed.Scheme.ToLowerInvariant(),
                    Host = parsed.IdnHost.ToLowerInvariant(),
                    Fragment = string.Empty
                };

                if (parsed.IsDefaultPort)
                {
                    builder.Port = -1;
                }

                return builder.Uri.AbsoluteUri;
            }
            catch
            {
                // Malformed or unusual inputs should miss the cache rather than make
                // the cache key constructor a source of navigation failures.
                return normalized;
            }
        }
    }
}
