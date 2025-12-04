using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network
{
    /// <summary>
    /// Represents the context for a single network request.
    /// Holds the request message, response (if any), and shared state for handlers.
    /// </summary>
    public class NetworkContext
    {
        public HttpRequestMessage Request { get; set; }
        public HttpResponseMessage Response { get; set; }
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();
        public bool IsBlocked { get; set; }
        public string BlockReason { get; set; }

        public NetworkContext(HttpRequestMessage request)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
        }
    }

    /// <summary>
    /// Interface for a network middleware handler.
    /// </summary>
    public interface INetworkHandler
    {
        /// <summary>
        /// Process the request. Can modify the context, return a response directly, or call the next handler.
        /// </summary>
        Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct);
    }

    /// <summary>
    /// Interface for the main network client that consumers use.
    /// </summary>
    public interface INetworkClient
    {
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
        Task<string> GetStringAsync(string url, CancellationToken ct = default);
        Task<Stream> GetStreamAsync(string url, CancellationToken ct = default);
        Task<byte[]> GetByteArrayAsync(string url, CancellationToken ct = default);
    }
}
