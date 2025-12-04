using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network
{
    public class NetworkClient : INetworkClient
    {
        private readonly List<INetworkHandler> _handlers;

        public NetworkClient(IEnumerable<INetworkHandler> handlers)
        {
            _handlers = handlers.ToList();
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var context = new NetworkContext(request);
            await ExecutePipelineAsync(context, 0, ct);
            return context.Response ?? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { ReasonPhrase = "No response generated" };
        }

        private async Task ExecutePipelineAsync(NetworkContext context, int index, CancellationToken ct)
        {
            if (index >= _handlers.Count)
            {
                return;
            }

            var handler = _handlers[index];
            await handler.HandleAsync(context, () => ExecutePipelineAsync(context, index + 1, ct), ct);
        }

        public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await SendAsync(req, ct))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
        }

        public async Task<Stream> GetStreamAsync(string url, CancellationToken ct = default)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            var resp = await SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStreamAsync();
        }

        public async Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var resp = await SendAsync(req, ct))
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync();
            }
        }
    }
}
