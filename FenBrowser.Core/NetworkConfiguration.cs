using System;

namespace FenBrowser.Core
{
    /// <summary>
    /// Centralized network configuration for HTTP/2, Brotli, and cache settings.
    /// Follows FenBrowser's modularity principle - all network settings in one place.
    /// </summary>
    public sealed class NetworkConfiguration
    {
        private static readonly Lazy<NetworkConfiguration> _instance = 
            new Lazy<NetworkConfiguration>(() => new NetworkConfiguration());
        
        public static NetworkConfiguration Instance => _instance.Value;

        private NetworkConfiguration() { }

        // ========== HTTP/2 Settings ==========
        
        /// <summary>
        /// Enable HTTP/2 protocol. Provides multiplexing, header compression, and prioritization.
        /// </summary>
        public bool EnableHttp2 { get; set; } = true;

        /// <summary>
        /// Enable HTTP/3 (QUIC) protocol. Future support.
        /// </summary>
        public bool EnableHttp3 { get; set; } = false;

        /// <summary>
        /// Maximum concurrent connections per server for HTTP/2 multiplexing.
        /// </summary>
        public int MaxConnectionsPerServer { get; set; } = 10;

        /// <summary>
        /// Connection timeout in seconds.
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Request timeout for document fetches.
        /// </summary>
        public int DocumentTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Request timeout for resource fetches (images, scripts, etc).
        /// </summary>
        public int ResourceTimeoutSeconds { get; set; } = 30;

        // ========== Compression Settings ==========

        /// <summary>
        /// Enable Brotli compression. Provides 15-25% better compression than GZIP.
        /// </summary>
        public bool EnableBrotli { get; set; } = true;

        /// <summary>
        /// Enable GZIP compression.
        /// </summary>
        public bool EnableGzip { get; set; } = true;

        /// <summary>
        /// Enable Deflate compression.
        /// </summary>
        public bool EnableDeflate { get; set; } = true;

        /// <summary>
        /// Gets the Accept-Encoding header value based on current settings.
        /// </summary>
        public string GetAcceptEncodingHeader()
        {
            var encodings = new System.Collections.Generic.List<string>();
            
            if (EnableBrotli)
                encodings.Add("br");
            if (EnableGzip)
                encodings.Add("gzip");
            if (EnableDeflate)
                encodings.Add("deflate");
            
            return encodings.Count > 0 ? string.Join(", ", encodings) : "identity";
        }

        // ========== Cache Settings (Memory Management) ==========

        /// <summary>
        /// Maximum memory for image cache in bytes. Default 100MB.
        /// </summary>
        public long MaxImageCacheBytes { get; set; } = 100 * 1024 * 1024;

        /// <summary>
        /// Maximum memory for text cache (HTML, CSS, JS) in bytes. Default 50MB.
        /// </summary>
        public long MaxTextCacheBytes { get; set; } = 50 * 1024 * 1024;

        /// <summary>
        /// Maximum number of images in cache. Prevents unbounded growth.
        /// </summary>
        public int MaxImageCacheCount { get; set; } = 500;

        /// <summary>
        /// Maximum number of text resources in cache.
        /// </summary>
        public int MaxTextCacheCount { get; set; } = 256;

        /// <summary>
        /// Cache TTL (time to live) for resources.
        /// </summary>
        public TimeSpan CacheTTL { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Disk cache size limit in bytes. Default 500MB.
        /// </summary>
        public long MaxDiskCacheBytes { get; set; } = 500 * 1024 * 1024;

        // ========== Lazy Loading Settings ==========

        /// <summary>
        /// Enable native lazy loading for images and iframes.
        /// </summary>
        public bool EnableLazyLoading { get; set; } = true;

        /// <summary>
        /// Distance from viewport (in pixels) at which lazy elements start loading.
        /// </summary>
        public int LazyLoadThresholdPx { get; set; } = 200;

        /// <summary>
        /// Maximum concurrent lazy load requests.
        /// </summary>
        public int MaxConcurrentLazyLoads { get; set; } = 4;

        // ========== Tab Suspension Settings ==========

        /// <summary>
        /// Enable automatic tab suspension for memory management.
        /// </summary>
        public bool EnableTabSuspension { get; set; } = true;

        /// <summary>
        /// Minutes of inactivity before a tab is suspended.
        /// </summary>
        public int TabSuspensionMinutes { get; set; } = 10;

        /// <summary>
        /// Minimum number of tabs that must remain active (never suspended).
        /// </summary>
        public int MinActiveTabs { get; set; } = 1;

        /// <summary>
        /// Memory threshold (bytes) above which aggressive suspension kicks in.
        /// </summary>
        public long AggressiveSuspensionThreshold { get; set; } = 500 * 1024 * 1024; // 500MB

        // ========== Statistics ==========

        /// <summary>
        /// Enable network statistics tracking for DevTools.
        /// </summary>
        public bool EnableNetworkStats { get; set; } = true;

        /// <summary>
        /// Log HTTP/2 connection details.
        /// </summary>
        public bool LogHttp2Details { get; set; } = false;

        /// <summary>
        /// Log compression ratios.
        /// </summary>
        public bool LogCompressionStats { get; set; } = false;

        // ========== Utility Methods ==========

        /// <summary>
        /// Get the HTTP version to use based on settings.
        /// </summary>
        public Version GetPreferredHttpVersion()
        {
            if (EnableHttp3)
                return System.Net.HttpVersion.Version30;
            if (EnableHttp2)
                return System.Net.HttpVersion.Version20;
            return System.Net.HttpVersion.Version11;
        }

        /// <summary>
        /// Get decompression methods based on settings.
        /// </summary>
        public System.Net.DecompressionMethods GetDecompressionMethods()
        {
            var methods = System.Net.DecompressionMethods.None;

            if (EnableBrotli)
                methods |= System.Net.DecompressionMethods.Brotli;
            if (EnableGzip)
                methods |= System.Net.DecompressionMethods.GZip;
            if (EnableDeflate)
                methods |= System.Net.DecompressionMethods.Deflate;

            return methods;
        }

        /// <summary>
        /// Reset all settings to defaults.
        /// </summary>
        public void ResetToDefaults()
        {
            EnableHttp2 = true;
            EnableHttp3 = false;
            MaxConnectionsPerServer = 10;
            ConnectionTimeoutSeconds = 30;
            DocumentTimeoutSeconds = 60;
            ResourceTimeoutSeconds = 30;
            EnableBrotli = true;
            EnableGzip = true;
            EnableDeflate = true;
            MaxImageCacheBytes = 100 * 1024 * 1024;
            MaxTextCacheBytes = 50 * 1024 * 1024;
            MaxImageCacheCount = 500;
            MaxTextCacheCount = 256;
            CacheTTL = TimeSpan.FromMinutes(30);
            MaxDiskCacheBytes = 500 * 1024 * 1024;
            EnableLazyLoading = true;
            LazyLoadThresholdPx = 200;
            MaxConcurrentLazyLoads = 4;
            EnableTabSuspension = true;
            TabSuspensionMinutes = 10;
            MinActiveTabs = 1;
            AggressiveSuspensionThreshold = 500 * 1024 * 1024;
            EnableNetworkStats = true;
            LogHttp2Details = false;
            LogCompressionStats = false;
        }
    }
}
