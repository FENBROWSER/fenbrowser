using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;

namespace FenBrowser.FenEngine.Rendering;

public class BrowserEngine : IBrowserEngine
{
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly INetworkService _networkService;
    private readonly ILogger _logger;
    private readonly object _navigationLock = new();
    private CancellationTokenSource _activeNavigation;
    private long _navigationGeneration;

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

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsNetworkUri(uri))
        {
            throw new ArgumentException("URL must be an absolute URI.", nameof(url));
        }

        await LoadAsync(uri).ConfigureAwait(false);
    }

    public async Task LoadAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!IsNetworkUri(uri))
            throw new ArgumentException("URI must be an absolute HTTP or HTTPS URL.", nameof(uri));

        var generation = Interlocked.Increment(ref _navigationGeneration);
        var navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource previous;
        lock (_navigationLock)
        {
            previous = _activeNavigation;
            _activeNavigation = navigation;
            Url = uri.AbsoluteUri;
            LastError = string.Empty;
            LoadState = BrowserEngineLoadState.Loading;
        }
        previous?.Cancel();
        _logger.Log(LogLevel.Info, $"Loading URL: {uri.AbsoluteUri}");

        try
        {
            string content = await _networkService.GetStringAsync(uri, navigation.Token).ConfigureAwait(false) ?? string.Empty;
            navigation.Token.ThrowIfCancellationRequested();
            _logger.Log(LogLevel.Info, $"Content loaded, length: {content.Length}");

            var document = HtmlParser.ParseDocument(content, uri);
            navigation.Token.ThrowIfCancellationRequested();
            var title = ResolveTitle(document.Title, uri);

            lock (_navigationLock)
            {
                EnsureCurrentNavigation(generation, navigation);
                Title = title;
                LoadState = BrowserEngineLoadState.Complete;
            }
            _logger.Log(LogLevel.Debug, $"Resolved title: {title}");
        }
        catch (OperationCanceledException ex)
        {
            lock (_navigationLock)
            {
                if (generation == _navigationGeneration)
                {
                    LastError = ex.Message;
                    Title = "Load cancelled";
                    LoadState = BrowserEngineLoadState.Cancelled;
                }
            }
            _logger.Log(LogLevel.Warn, $"Cancelled load for {uri.AbsoluteUri}");
            throw;
        }
        catch (Exception ex)
        {
            lock (_navigationLock)
            {
                if (generation != _navigationGeneration || !ReferenceEquals(_activeNavigation, navigation))
                {
                    throw new OperationCanceledException("Navigation was superseded.", ex, navigation.Token);
                }

                LastError = ex.Message;
                Title = "Error loading page";
                LoadState = BrowserEngineLoadState.Failed;
            }
            _logger.LogError($"Failed to load {uri.AbsoluteUri}", ex);
            throw new NavigationException(uri, ex);
        }
        finally
        {
            lock (_navigationLock)
            {
                if (ReferenceEquals(_activeNavigation, navigation))
                {
                    _activeNavigation = null;
                }
            }
            navigation.Dispose();
        }
    }

    private void EnsureCurrentNavigation(long generation, CancellationTokenSource navigation)
    {
        if (generation != _navigationGeneration || !ReferenceEquals(_activeNavigation, navigation))
        {
            throw new OperationCanceledException("Navigation was superseded.", navigation.Token);
        }
    }

    private static string ResolveTitle(string documentTitle, Uri uri)
    {
        var normalizedTitle = WhitespaceRegex.Replace(documentTitle ?? string.Empty, " ").Trim();
        if (!string.IsNullOrWhiteSpace(normalizedTitle))
        {
            const int maxTitleLength = 256;
            return normalizedTitle.Length > maxTitleLength
                ? normalizedTitle.Substring(0, maxTitleLength)
                : normalizedTitle;
        }

        return !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : uri.AbsoluteUri;
    }

    private static bool IsNetworkUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}
