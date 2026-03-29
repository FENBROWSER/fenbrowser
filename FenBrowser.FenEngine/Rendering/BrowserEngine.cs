using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering;

public class BrowserEngine : IBrowserEngine
{
    private static readonly Regex TitleRegex = new(
        @"<title\b[^>]*>(?<title>.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(500));

    private readonly INetworkService _networkService;
    private readonly ILogger _logger;

    public string Title { get; private set; } = "New Tab";
    public string Url { get; private set; } = string.Empty;
    public BrowserEngineLoadState LoadState { get; private set; } = BrowserEngineLoadState.Idle;
    public string LastError { get; private set; } = string.Empty;
    public bool IsLoading => LoadState == BrowserEngineLoadState.Loading;

    public BrowserEngine(INetworkService networkService, ILogger logger)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task LoadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL cannot be null or whitespace.", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("URL must be an absolute URI.", nameof(url));
        }

        await LoadAsync(uri).ConfigureAwait(false);
    }

    public async Task LoadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("URI must be absolute.", nameof(uri));

        Url = uri.AbsoluteUri;
        LastError = string.Empty;
        LoadState = BrowserEngineLoadState.Loading;
        _logger.Log(LogLevel.Info, $"Loading URL: {uri.AbsoluteUri}");

        try
        {
            string content = await _networkService.GetStringAsync(uri, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            _logger.Log(LogLevel.Info, $"Content loaded, length: {content.Length}");

            Title = ExtractTitle(content, uri.AbsoluteUri);
            LoadState = BrowserEngineLoadState.Complete;
            _logger.Log(LogLevel.Debug, $"Resolved title: {Title}");
        }
        catch (OperationCanceledException ex)
        {
            LastError = ex.Message;
            Title = "Load cancelled";
            LoadState = BrowserEngineLoadState.Cancelled;
            _logger.Log(LogLevel.Warn, $"Cancelled load for {uri.AbsoluteUri}");
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Title = "Error loading page";
            LoadState = BrowserEngineLoadState.Failed;
            _logger.LogError($"Failed to load {uri.AbsoluteUri}", ex);
        }
    }

    private static string ExtractTitle(string html, string url)
    {
        if (!string.IsNullOrWhiteSpace(html))
        {
            var match = TitleRegex.Match(html);
            if (match.Success)
            {
                string rawTitle = match.Groups["title"].Value;
                string decodedTitle = WebUtility.HtmlDecode(rawTitle);
                string normalizedTitle = Regex.Replace(decodedTitle ?? string.Empty, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(normalizedTitle))
                {
                    const int maxTitleLength = 256;
                    return normalizedTitle.Length > maxTitleLength
                        ? normalizedTitle.Substring(0, maxTitleLength)
                        : normalizedTitle;
                }
            }
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host;
        }

        return url;
    }
}
