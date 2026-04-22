// =============================================================================
// NavigationCommands.cs
// W3C WebDriver Navigation Commands
// 
// SPEC REFERENCE: W3C WebDriver §10 - Navigation
//                 https://www.w3.org/TR/webdriver2/#navigation
// =============================================================================

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Navigation commands.
    /// </summary>
    public class NavigationCommands
    {
        private const int DefaultPageLoadTimeoutMs = 30_000;
        private const int MaxPageLoadTimeoutMs = 60_000;
        private static readonly TimeSpan NavigationPollInterval = TimeSpan.FromMilliseconds(25);
        private readonly CommandHandler _handler;
        
        public NavigationCommands(CommandHandler handler)
        {
            _handler = handler;
        }
        
        /// <summary>
        /// Navigate to URL.
        /// POST /session/{sessionId}/url
        /// </summary>
        public async Task<WebDriverResponse> NavigateToAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);

            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object || !body.Value.TryGetProperty("url", out var urlElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "URL is required");
            }

            var url = urlElement.GetString();
            if (string.IsNullOrEmpty(url))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "URL cannot be empty");
            }

            if (!_handler.IsNavigationAllowed(sessionId, url))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"Navigation blocked by security policy: {url}");
            }
            
            if (!Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"Invalid URL: {url}");
            }

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var strategy = Capabilities.NormalizePageLoadStrategy(session.Capabilities?.PageLoadStrategy);
            string startingUrl = await _handler.Browser.GetCurrentUrlAsync();

            await _handler.Browser.NavigateAsync(absoluteUri.AbsoluteUri);

            // pageLoadStrategy=none returns as soon as navigation is initiated.
            if (string.Equals(strategy, "none", StringComparison.Ordinal))
            {
                return WebDriverResponse.Success(null);
            }

            var timeoutMs = ResolvePageLoadTimeoutMs(session.Timeouts?.PageLoad);
            var settledUrl = await WaitForNavigationCommitAsync(startingUrl, absoluteUri.AbsoluteUri, timeoutMs);
            if (IsAboutBlank(settledUrl))
            {
                throw new WebDriverException(
                    ErrorCodes.Timeout,
                    $"Navigation did not commit a non-blank URL within {timeoutMs}ms: {absoluteUri.AbsoluteUri}");
            }

            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Get current URL.
        /// GET /session/{sessionId}/url
        /// </summary>
        public async Task<WebDriverResponse> GetCurrentUrlAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            
            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success("about:blank");
            }
            
            var url = await _handler.Browser.GetCurrentUrlAsync();
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "about:blank";
            }

            return WebDriverResponse.Success(url);
        }
        
        /// <summary>
        /// Go back.
        /// POST /session/{sessionId}/back
        /// </summary>
        public async Task<WebDriverResponse> BackAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            
            if (_handler.Browser != null)
            {
                await _handler.Browser.GoBackAsync();
            }
            
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Go forward.
        /// POST /session/{sessionId}/forward
        /// </summary>
        public async Task<WebDriverResponse> ForwardAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            
            if (_handler.Browser != null)
            {
                await _handler.Browser.GoForwardAsync();
            }
            
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Refresh page.
        /// POST /session/{sessionId}/refresh
        /// </summary>
        public async Task<WebDriverResponse> RefreshAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            
            if (_handler.Browser != null)
            {
                await _handler.Browser.RefreshAsync();
            }
            
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Get page title.
        /// GET /session/{sessionId}/title
        /// </summary>
        public async Task<WebDriverResponse> GetTitleAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            
            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success("");
            }
            
            var title = await _handler.Browser.GetTitleAsync();
            return WebDriverResponse.Success(title ?? "");
        }

        private async Task<string> WaitForNavigationCommitAsync(string previousUrl, string requestedUrl, int timeoutMs)
        {
            var cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                var currentUrl = await _handler.Browser.GetCurrentUrlAsync();
                if (IsNavigationCommitted(previousUrl, requestedUrl, currentUrl))
                {
                    return currentUrl;
                }

                try
                {
                    await Task.Delay(NavigationPollInterval, cts.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            throw new WebDriverException(
                ErrorCodes.Timeout,
                $"Timed out after {timeoutMs}ms waiting for navigation to commit to {requestedUrl}");
        }

        private static bool IsNavigationCommitted(string previousUrl, string requestedUrl, string currentUrl)
        {
            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                return false;
            }

            if (UrlsEquivalent(currentUrl, requestedUrl))
            {
                return true;
            }

            // Redirects are valid completion signals once we leave the previous URL and are no longer blank.
            if (!IsAboutBlank(currentUrl) &&
                !UrlsEquivalent(currentUrl, previousUrl))
            {
                return true;
            }

            return false;
        }

        private static bool IsAboutBlank(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return true;
            }

            return url.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);
        }

        private static bool UrlsEquivalent(string left, string right)
        {
            var normalizedLeft = NormalizeComparableUrl(left);
            var normalizedRight = NormalizeComparableUrl(right);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeComparableUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "about:blank";
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return url.Trim();
            }

            // Normalize root slash differences (https://a and https://a/).
            return uri.AbsoluteUri.TrimEnd('/');
        }

        private static int ResolvePageLoadTimeoutMs(int? configuredTimeoutMs)
        {
            var value = configuredTimeoutMs ?? DefaultPageLoadTimeoutMs;
            if (value <= 0)
            {
                return DefaultPageLoadTimeoutMs;
            }

            return Math.Min(value, MaxPageLoadTimeoutMs);
        }
    }
}
