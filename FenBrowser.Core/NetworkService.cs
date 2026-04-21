using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security;
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
        
        EngineLogCompat.Log($"[Loader] {response.RequestMessage.Method} {response.RequestMessage.RequestUri}", LogCategory.Network);
        EngineLogCompat.Log($"[Loader] Status: {(int)response.StatusCode} {response.ReasonPhrase}", LogCategory.Network);
        EngineLogCompat.Log($"[Loader] MIME: {mime}", LogCategory.Network);
        EngineLogCompat.Log($"[Loader] Encoding: {encoding}", LogCategory.Network);
        
        if (!string.IsNullOrEmpty(contentEncoding))
            EngineLogCompat.Log($"[Loader] Compression: {contentEncoding}", LogCategory.Network);
    }

    public Task<Stream> GetStreamAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid network URL: {url}");
        }

        return GetStreamAsync(uri);
    }

    public Task<string> GetStringAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid network URL: {url}");
        }

        return GetStringAsync(uri);
    }

    public async Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException($"Invalid network URL: {uri}");

        var configuration = NetworkConfiguration.Instance;
        configuration.ValidateOrThrow();
        EnforceSecurityPolicy(uri);

        using var logScope = EngineLogCompat.BeginScope(
            component: "NetworkService",
            data: new System.Collections.Generic.Dictionary<string, object>
            {
                ["url"] = uri.AbsoluteUri,
                ["mode"] = "stream"
            });

        using var request = CreateRequest(uri, configuration);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.ResourceTimeoutSeconds));

        if (DebugConfig.LogResourceLoader)
            EngineLogCompat.Log($"[Loader] GET {uri}", LogCategory.Network);

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        LogResponse(response);
        return await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException($"Invalid network URL: {uri}");

        var configuration = NetworkConfiguration.Instance;
        configuration.ValidateOrThrow();
        EnforceSecurityPolicy(uri);

        using var logScope = EngineLogCompat.BeginScope(
            component: "NetworkService",
            data: new System.Collections.Generic.Dictionary<string, object>
            {
                ["url"] = uri.AbsoluteUri,
                ["mode"] = "string"
            });

        using var request = CreateRequest(uri, configuration);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.DocumentTimeoutSeconds));

        if (DebugConfig.LogResourceLoader)
            EngineLogCompat.Log($"[Loader] GET {uri}", LogCategory.Network);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            timeoutCts.Token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        LogResponse(response);
        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private static void EnforceSecurityPolicy(Uri uri)
    {
        var decision = BrowserSecurityPolicy.EvaluateNetworkRequest(uri);
        if (!decision.IsAllowed)
        {
            decision.Log();
            throw new InvalidOperationException(decision.Message);
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri, NetworkConfiguration configuration)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = configuration.GetPreferredHttpVersion(),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        BrowserSettings.ApplyBrowserRequestHeaders(request);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", configuration.GetAcceptEncodingHeader());
        return request;
    }
}
