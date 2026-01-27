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
            PartitionKey = partitionKey ?? "default";
            Url = url ?? string.Empty;
        }

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
            // FNV-1a style combination or HashCode.Combine
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (PartitionKey?.GetHashCode() ?? 0);
                hash = hash * 31 + (Url?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public override string ToString() => $"[{PartitionKey}] {Url}";
    }
}
