using System;
using System.Net;
using System.Net.Http;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Factory for creating HTTP clients with HTTP/2 and Brotli support.
    /// Centralizes all HttpClient configuration for consistency.
    /// </summary>
    public static class HttpClientFactory
    {
        private static readonly object _lock = new object();
        private static HttpClient _sharedClient;
        private static HttpClientHandler _sharedHandler;

        /// <summary>
        /// Gets or creates a shared HttpClient with HTTP/2 and Brotli support.
        /// Thread-safe singleton pattern.
        /// </summary>
        public static HttpClient GetSharedClient()
        {
            if (_sharedClient != null)
                return _sharedClient;

            lock (_lock)
            {
                if (_sharedClient == null)
                {
                    _sharedHandler = CreateHandler();
                    _sharedClient = CreateClient(_sharedHandler);
                }
                return _sharedClient;
            }
        }

        /// <summary>
        /// Creates a new HttpClientHandler with Brotli and other compression support.
        /// </summary>
        public static HttpClientHandler CreateHandler()
        {
            var config = NetworkConfiguration.Instance;
            
            var handler = new HttpClientHandler
            {
                // Enable all compression methods including Brotli
                AutomaticDecompression = config.GetDecompressionMethods(),
                
                // Explicit proxy toggle (avoid inheriting dead localhost proxies in lab setups)
                UseProxy = config.UseSystemProxy,
                
                // We handle redirects manually for better control
                AllowAutoRedirect = false,
                
                // Connection pooling for HTTP/2 multiplexing
                MaxConnectionsPerServer = config.MaxConnectionsPerServer,
                
                // SSL/TLS settings
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | 
                               System.Security.Authentication.SslProtocols.Tls13,
                
                // Keep-alive for connection reuse
                UseCookies = false, // We handle cookies manually for privacy
            };

            return handler;
        }

        /// <summary>
        /// Creates an HttpClient with HTTP/2 as default version.
        /// </summary>
        public static HttpClient CreateClient(HttpClientHandler handler = null)
        {
            var config = NetworkConfiguration.Instance;
            handler ??= CreateHandler();

            var client = new HttpClient(handler)
            {
                // Use HTTP/2 by default, fall back to HTTP/1.1 if server doesn't support
                DefaultRequestVersion = config.GetPreferredHttpVersion(),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                
                // Global timeout
                Timeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds)
            };

            // Set default headers
            client.DefaultRequestHeaders.ConnectionClose = false; // Keep-alive
            
            // Set User-Agent from Settings
            var uaString = BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
            if (!string.IsNullOrEmpty(uaString))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", uaString);
            }
            
            // Log configuration if debugging enabled
            if (config.LogHttp2Details)
            {
                FenLogger.Info($"[HttpClientFactory] Created client: HTTP/{config.GetPreferredHttpVersion()}, " +
                              $"Compression={config.GetDecompressionMethods()}, " +
                              $"MaxConnections={config.MaxConnectionsPerServer}", 
                              Logging.LogCategory.Network);
            }

            return client;
        }

        /// <summary>
        /// Creates an HttpClient for private browsing (no persistent connections).
        /// </summary>
        public static HttpClient CreatePrivateClient()
        {
            var handler = CreateHandler();
            
            // Additional privacy settings
            handler.UseCookies = false;
            handler.UseDefaultCredentials = false;
            
            var client = CreateClient(handler);
            
            // Shorter timeout for private mode
            client.Timeout = TimeSpan.FromSeconds(15);
            
            return client;
        }

        /// <summary>
        /// Disposes the shared client. Call on application shutdown.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                _sharedClient?.Dispose();
                _sharedClient = null;
                _sharedHandler?.Dispose();
                _sharedHandler = null;
            }
        }

        /// <summary>
        /// Gets statistics about the current HTTP client configuration.
        /// </summary>
        public static string GetConfigurationSummary()
        {
            var config = NetworkConfiguration.Instance;
            return $"HTTP Version: {config.GetPreferredHttpVersion()}, " +
                   $"Compression: {config.GetDecompressionMethods()}, " +
                   $"Max Connections: {config.MaxConnectionsPerServer}";
        }
    }
}
