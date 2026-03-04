using System;
using System.Net;
using System.Text.RegularExpressions;
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

        _logger.Log(LogLevel.Info, $"Loading URL: {url}");
        Url = url;

        try
        {
            string content = await _networkService.GetStringAsync(url).ConfigureAwait(false) ?? string.Empty;
            _logger.Log(LogLevel.Info, $"Content loaded, length: {content.Length}");

            Title = ExtractTitle(content, url);
            _logger.Log(LogLevel.Debug, $"Resolved title: {Title}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to load {url}", ex);
            Title = "Error loading page";
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
