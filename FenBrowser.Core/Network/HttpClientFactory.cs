using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

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
        private static SocketsHttpHandler _sharedHandler;

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
        public static SocketsHttpHandler CreateHandler()
        {
            var config = NetworkConfiguration.Instance;
            
            var handler = new SocketsHttpHandler
            {
                // Enable all compression methods including Brotli
                AutomaticDecompression = config.GetDecompressionMethods(),
                
                // Explicit proxy toggle (avoid inheriting dead localhost proxies in lab setups)
                UseProxy = config.UseSystemProxy,
                
                // We handle redirects manually for better control
                AllowAutoRedirect = false,
                
                // Connection pooling for HTTP/2 multiplexing
                MaxConnectionsPerServer = config.MaxConnectionsPerServer,
                
                // Keep-alive for connection reuse
                UseCookies = false, // We handle cookies manually for privacy
            };
            handler.SslOptions.EnabledSslProtocols =
                System.Security.Authentication.SslProtocols.Tls12 |
                System.Security.Authentication.SslProtocols.Tls13;

            if (BrowserSettings.Instance.UseSecureDNS)
            {
                handler.ConnectCallback = ConnectWithSecureDnsAsync;
            }

            return handler;
        }

        /// <summary>
        /// Creates an HttpClient with HTTP/2 as default version.
        /// </summary>
        public static HttpClient CreateClient(SocketsHttpHandler handler = null)
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
            handler.Credentials = null;
            
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

        public static void ConfigureServerCertificateValidation(
            SocketsHttpHandler handler,
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> callback)
        {
            if (handler == null || callback == null)
            {
                return;
            }

            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
            {
                try
                {
                    var cert2 = cert as X509Certificate2;
                    if (cert2 == null && cert != null)
                    {
                        cert2 = new X509Certificate2(cert);
                    }
                    return callback(null, cert2, chain, errors);
                }
                catch
                {
                    return false;
                }
            };
        }

        private static async ValueTask<System.IO.Stream> ConnectWithSecureDnsAsync(
            SocketsHttpConnectionContext context,
            CancellationToken ct)
        {
            var endPoint = context?.DnsEndPoint;
            if (endPoint == null)
            {
                throw new InvalidOperationException("Missing DNS endpoint for HTTP connection.");
            }

            var resolvedIp = await SecureDnsResolver.ResolveAsync(endPoint.Host, ct).ConfigureAwait(false);
            if (resolvedIp != null)
            {
                try
                {
                    return await ConnectSocketAsync(new IPEndPoint(resolvedIp, endPoint.Port), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn(
                        $"[SecureDNS] Direct connect via DoH-resolved IP failed for {endPoint.Host}:{endPoint.Port}: {ex.Message}. Falling back to system resolver.",
                        Logging.LogCategory.Network);
                }
            }

            return await ConnectSocketAsync(endPoint, ct).ConfigureAwait(false);
        }

        private static async Task<System.IO.Stream> ConnectSocketAsync(EndPoint endPoint, CancellationToken ct)
        {
            try
            {
                switch (endPoint)
                {
                    case IPEndPoint ipEndPoint:
                    {
                        var socket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };

                        try
                        {
                            await socket.ConnectAsync(ipEndPoint, ct).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                    case DnsEndPoint dnsEndPoint:
                    {
                        var client = new TcpClient();
                        client.NoDelay = true;
                        try
                        {
                            await client.ConnectAsync(dnsEndPoint.Host, dnsEndPoint.Port, ct).ConfigureAwait(false);
                            return client.GetStream();
                        }
                        catch
                        {
                            client.Dispose();
                            throw;
                        }
                    }
                    default:
                        throw new NotSupportedException($"Unsupported endpoint type: {endPoint?.GetType().Name}");
                }
            }
            catch
            {
                throw;
            }
        }
    }
}
