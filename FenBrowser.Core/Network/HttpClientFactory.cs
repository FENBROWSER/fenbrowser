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
        private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _requestTransport;

        /// <summary>
        /// Installs a process-local request transport used by clients created after
        /// this call. The Host uses this seam to route browser traffic through the
        /// sandboxed network process without introducing a Core -> Host dependency.
        /// Replacing the transport retires the shared client so callers cannot keep
        /// acquiring a client bound to the previous security boundary.
        /// </summary>
        public static void ConfigureRequestTransport(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));

            lock (_lock)
            {
                Volatile.Write(ref _requestTransport, transport);
                DisposeSharedClientLocked();
            }
        }

        /// <summary>
        /// Restores direct in-process HTTP transport for subsequently created clients.
        /// Existing shared-client references are disposed before the new mode is used.
        /// </summary>
        public static void ClearRequestTransport()
        {
            lock (_lock)
            {
                Volatile.Write(ref _requestTransport, null);
                DisposeSharedClientLocked();
            }
        }

        /// <summary>
        /// Gets or creates a shared HttpClient with HTTP/2 and Brotli support.
        /// Thread-safe singleton pattern.
        /// </summary>
        public static HttpClient GetSharedClient()
        {
            lock (_lock)
            {
                if (_sharedClient == null)
                {
                    if (Volatile.Read(ref _requestTransport) == null)
                    {
                        _sharedHandler = CreateHandler();
                        _sharedClient = CreateClient(_sharedHandler);
                    }
                    else
                    {
                        _sharedHandler = null;
                        _sharedClient = CreateClient();
                    }
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

                // Connection establishment has its own deadline. Higher-level fetch
                // code owns document/resource request deadlines.
                ConnectTimeout = TimeSpan.FromSeconds(config.ConnectionTimeoutSeconds),
                
                // Keep-alive for connection reuse
                UseCookies = false, // We handle cookies manually for privacy
            };

            // Leave SslOptions.EnabledSslProtocols at its default (None) so the
            // operating system can apply current TLS policy and adopt newer TLS versions.

            if (BrowserSettings.Instance.UseSecureDNS)
            {
                handler.ConnectCallback = ConnectWithSecureDnsAsync;
            }

            return handler;
        }

        /// <summary>
        /// Creates an HttpClient with HTTP/2 as default version. When the Host has
        /// installed a broker transport, the returned client delegates requests to
        /// that boundary instead of opening sockets in this process.
        /// </summary>
        public static HttpClient CreateClient(SocketsHttpHandler handler = null)
        {
            var config = NetworkConfiguration.Instance;
            var transport = Volatile.Read(ref _requestTransport);
            HttpMessageHandler effectiveHandler;

            if (transport != null)
            {
                // A caller may have prepared a direct socket handler before the Host
                // installed the broker transport. It must not remain live as a hidden
                // bypass path once brokered networking is authoritative.
                handler?.Dispose();
                effectiveHandler = new RequestTransportHandler(transport);
            }
            else
            {
                effectiveHandler = handler ?? CreateHandler();
            }

            var client = new HttpClient(effectiveHandler)
            {
                // Use HTTP/2 by default, fall back to HTTP/1.1 if server doesn't support
                DefaultRequestVersion = config.GetPreferredHttpVersion(),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                
                // Per-request/document deadlines are enforced by callers. Keeping the
                // shared client timeout infinite avoids a second, unrelated global timer.
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
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
                EngineLogCompat.Info($"[HttpClientFactory] Created client: HTTP/{config.GetPreferredHttpVersion()}, " +
                              $"Compression={config.GetDecompressionMethods()}, " +
                              $"MaxConnections={config.MaxConnectionsPerServer}, " +
                              $"Transport={(transport == null ? "direct" : "brokered")}",
                              Logging.LogCategory.Network);
            }

            return client;
        }

        /// <summary>
        /// Creates an HttpClient for private browsing.
        /// </summary>
        public static HttpClient CreatePrivateClient()
        {
            var handler = CreateHandler();
            
            // Additional privacy settings
            handler.UseCookies = false;
            handler.Credentials = null;
            
            // Private mode uses the same caller-owned document/resource deadlines as
            // normal browsing. Do not add a second whole-request HttpClient timeout.
            return CreateClient(handler);
        }

        /// <summary>
        /// Disposes the shared client. Call on application shutdown.
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                DisposeSharedClientLocked();
            }
        }

        private static void DisposeSharedClientLocked()
        {
            _sharedClient?.Dispose();
            _sharedClient = null;
            _sharedHandler?.Dispose();
            _sharedHandler = null;
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
                    EngineLogCompat.Warn(
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

        private sealed class RequestTransportHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _transport;

            public RequestTransportHandler(
                Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> transport)
            {
                _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return _transport(request, cancellationToken);
            }
        }
    }
}
