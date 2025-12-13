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
        // Don't set default UA here - we'll use BrowserSettings per-request
    }
    
    /// <summary>
    /// Gets the current User-Agent from BrowserSettings
    /// </summary>
    private string GetCurrentUserAgent()
    {
        return BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
    }

    public async Task<Stream> GetStreamAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", GetCurrentUserAgent());
        var response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<string> GetStringAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", GetCurrentUserAgent());
        var response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }
}
