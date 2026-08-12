// SpecRef: WHATWG Fetch, CORS request/response handling
// CapabilityId: FETCH-CORS-POLICY-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;

namespace FenBrowser.Core.Network.Handlers
{
    public class HttpHandler : INetworkHandler
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClient _noProxyClient;

        public HttpHandler(HttpClient httpClient = null)
        {
            // Use HttpClientFactory for HTTP/2 and Brotli support
            _httpClient = httpClient ?? HttpClientFactory.GetSharedClient();

            // Narrow fallback transport for the known dead localhost proxy used by
            // some development/lab environments. Other proxy failures must not
            // silently bypass an explicitly configured proxy boundary.
            var noProxyHandler = HttpClientFactory.CreateHandler();
            noProxyHandler.UseProxy = false;
            _noProxyClient = HttpClientFactory.CreateClient(noProxyHandler);
        }


        public async Task HandleAsync(NetworkContext context, System.Func<Task> next, CancellationToken ct)
        {
            if (context.Response != null) return; // Already handled

            try
            {
                context.Response = await SendWithProxyFallbackAsync(context.Request, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Handle timeout/cancellation
                throw;
            }
            catch (System.Exception)
            {
                // Log or wrap exception? For now, let it bubble up or set error response
                // context.Response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { ReasonPhrase = ex.Message };
                throw;
            }

            // Terminal handler, so we don't necessarily need to call next(), 
            // but good practice if we want post-processing handlers.
            await next();
        }

        private async Task<HttpResponseMessage> SendWithProxyFallbackAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ShouldRetryWithoutProxy(ex))
            {
                using var retryRequest = await CloneRequestAsync(request).ConfigureAwait(false);
                return await _noProxyClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
        }

        private static bool ShouldRetryWithoutProxy(HttpRequestException ex)
        {
            var msg = ex?.ToString() ?? string.Empty;
            return msg.Length > 0 && IsLoopbackProxyRefusal(msg);
        }

        private static bool IsLoopbackProxyRefusal(string msg)
        {
            return (msg.IndexOf("127.0.0.1:9", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("localhost:9", System.StringComparison.OrdinalIgnoreCase) >= 0) &&
                   (msg.IndexOf("refused", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("actively refused", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri)
            {
                Version = source.Version,
                VersionPolicy = source.VersionPolicy
            };

            foreach (var header in source.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (source.Content != null)
            {
                var bytes = await source.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var content = new ByteArrayContent(bytes);
                foreach (var header in source.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                clone.Content = content;
            }

            return clone;
        }
    }
}
