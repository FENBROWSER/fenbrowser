using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace FenBrowser.Core;

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;

    public NetworkService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "FenBrowser/1.0");
    }

    public async Task<Stream> GetStreamAsync(string url)
    {
        return await _httpClient.GetStreamAsync(url);
    }

    public async Task<string> GetStringAsync(string url)
    {
        return await _httpClient.GetStringAsync(url);
    }
}
