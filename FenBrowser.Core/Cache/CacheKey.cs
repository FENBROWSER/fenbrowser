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
            // Case-sensitive for efficiency and correctness (URLs usually are)
            // PartitionKey is usually normalized before creation
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
            return string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim();
        }
    }
}
