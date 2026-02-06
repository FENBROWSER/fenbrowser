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

            // Fallback transport for misconfigured/dead system proxy scenarios.
            var noProxyHandler = HttpClientFactory.CreateHandler();
            noProxyHandler.UseProxy = false;
            _noProxyClient = HttpClientFactory.CreateClient(noProxyHandler);
        }


        public async Task HandleAsync(NetworkContext context, System.Func<Task> next, CancellationToken ct)
        {
            if (context.Response != null) return; // Already handled

            try
            {
                context.Response = await _httpClient.SendAsync(context.Request, ct);
            }
            catch (TaskCanceledException)
            {
                // Handle timeout/cancellation
                throw;
            }
            catch (HttpRequestException ex) when (IsLoopbackProxyRefusal(ex))
            {
                // Retry once bypassing system proxy when request was routed to a dead local proxy endpoint
                // (for example 127.0.0.1:9).
                var retryRequest = await CloneRequestAsync(context.Request).ConfigureAwait(false);
                context.Response = await _noProxyClient.SendAsync(retryRequest, ct).ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                // Log or wrap exception? For now, let it bubble up or set error response
                // context.Response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { ReasonPhrase = ex.Message };
                throw;
            }

            // Terminal handler, so we don't necessarily need to call next(), 
            // but good practice if we want post-processing handlers.
            await next();
        }

        private static bool IsLoopbackProxyRefusal(HttpRequestException ex)
        {
            var msg = ex?.ToString() ?? string.Empty;
            if (msg.Length == 0) return false;
            return (msg.IndexOf("127.0.0.1:9", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    msg.IndexOf("localhost:9", System.StringComparison.OrdinalIgnoreCase) >= 0) &&
                   msg.IndexOf("refused", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri)
            {
                Version = source.Version
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
