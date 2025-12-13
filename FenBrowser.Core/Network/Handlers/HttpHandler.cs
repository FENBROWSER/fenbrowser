using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Network.Handlers
{
    public class HttpHandler : INetworkHandler
    {
        private readonly HttpClient _httpClient;

        public HttpHandler(HttpClient httpClient = null)
        {
            // Use HttpClientFactory for HTTP/2 and Brotli support
            _httpClient = httpClient ?? HttpClientFactory.GetSharedClient();
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
    }
}
