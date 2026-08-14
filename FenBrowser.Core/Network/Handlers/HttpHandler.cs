// SpecRef: WHATWG Fetch, CORS request/response handling
// CapabilityId: FETCH-CORS-POLICY-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    public sealed class HttpHandler : INetworkHandler
    {
        private readonly HttpClient _httpClient;

        public HttpHandler(HttpClient httpClient = null)
        {
            // Transport/proxy policy belongs to HttpClientFactory. A request handler
            // must never route around an explicitly configured proxy based on an
            // exception-message heuristic.
            _httpClient = httpClient ?? HttpClientFactory.GetSharedClient();
        }

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            if (context.Response != null)
            {
                return;
            }

            if (context.Request == null)
            {
                throw new InvalidOperationException("HTTP network context has no request.");
            }

            // ResponseHeadersRead preserves streaming semantics: ownership of the
            // response body remains with the caller/pipeline instead of buffering the
            // entire resource in this terminal transport handler.
            context.Response = await _httpClient.SendAsync(
                context.Request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            // Allow downstream post-processing handlers to inspect/filter the response.
            await next().ConfigureAwait(false);
        }
    }
}
