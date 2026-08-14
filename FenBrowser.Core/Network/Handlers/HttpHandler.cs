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
            var transportResponse = await _httpClient.SendAsync(
                context.Request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            context.Response = transportResponse;
            try
            {
                // Allow downstream post-processing handlers to inspect/filter the response.
                await next().ConfigureAwait(false);
            }
            catch
            {
                // SendAsync has not returned the response to its caller yet. If a
                // downstream CORS/privacy/safety handler fails, there is otherwise no
                // owner left to dispose the streaming body and release the pooled
                // connection. Dispose the transport response here and clear the
                // context only when it still points at that response.
                if (ReferenceEquals(context.Response, transportResponse))
                {
                    context.Response = null;
                }

                transportResponse.Dispose();
                throw;
            }

            // A downstream handler is allowed to replace the response. Once that
            // happens the original transport response is no longer externally
            // reachable, so release it rather than leaking its socket/body.
            if (!ReferenceEquals(context.Response, transportResponse))
            {
                transportResponse.Dispose();
            }
        }
    }
}
