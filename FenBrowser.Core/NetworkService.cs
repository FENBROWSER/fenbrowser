using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;
using System.Linq;

namespace FenBrowser.Core;

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;

    public NetworkService()
    {
        // Use HttpClientFactory for HTTP/2 and Brotli support
        _httpClient = HttpClientFactory.GetSharedClient();
    }

    /// <summary>
    /// Gets the current User-Agent from BrowserSettings
    /// </summary>
    private string GetCurrentUserAgent()
    {
        return BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
    }

    private void LogResponse(HttpResponseMessage response)
    {
        if (!DebugConfig.LogResourceLoader) return;
        
        var contentType = response.Content.Headers.ContentType;
        var encoding = contentType?.CharSet ?? "utf-8 (implicit)";
        var mime = contentType?.MediaType ?? "unknown";
        var contentEncoding = string.Join(", ", response.Content.Headers.ContentEncoding);
        
        FenLogger.Log($"[Loader] {response.RequestMessage.Method} {response.RequestMessage.RequestUri}", LogCategory.Network);
        FenLogger.Log($"[Loader] Status: {(int)response.StatusCode} {response.ReasonPhrase}", LogCategory.Network);
        FenLogger.Log($"[Loader] MIME: {mime}", LogCategory.Network);
        FenLogger.Log($"[Loader] Encoding: {encoding}", LogCategory.Network);
        
        if (!string.IsNullOrEmpty(contentEncoding))
            FenLogger.Log($"[Loader] Compression: {contentEncoding}", LogCategory.Network);
    }

    public async Task<Stream> GetStreamAsync(string url)
    {
        if (DebugConfig.LogResourceLoader)
            FenLogger.Log($"[Loader] GET {url}", LogCategory.Network);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", GetCurrentUserAgent());
        
        var response = await _httpClient.SendAsync(request);
        LogResponse(response);
        
        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<string> GetStringAsync(string url)
    {
        if (DebugConfig.LogResourceLoader)
            FenLogger.Log($"[Loader] GET {url}", LogCategory.Network);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", GetCurrentUserAgent());
        
        var response = await _httpClient.SendAsync(request);
        LogResponse(response);
        
        var content = await response.Content.ReadAsStringAsync();
        
        return content;
    }
}
