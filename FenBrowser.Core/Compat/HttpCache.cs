using System.Threading.Tasks;
using System.Net.Http;

namespace FenBrowser.Core.Compat
{
    // Minimal stub to satisfy Engine references when a full cache implementation
    // is not present. Always returns null so callers fall back to live HTTP.
    public sealed class HttpCache
    {
        private static readonly HttpCache _instance = new HttpCache();
        public static HttpCache Instance { get { return _instance; } }

        // System.Net.Http overloads (used by some engine code paths)
        public Task<string> GetStringAsync(HttpClient client, HttpRequestMessage req)
        {
            return Task.FromResult<string>(null);
        }
        public Task<byte[]> GetBufferAsync(HttpClient client, HttpRequestMessage req)
        {
            return Task.FromResult<byte[]>(null);
        }
    }
}
