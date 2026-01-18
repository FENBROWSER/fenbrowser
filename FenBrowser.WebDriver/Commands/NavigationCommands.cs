// =============================================================================
// NavigationCommands.cs
// W3C WebDriver Navigation Commands
// 
// SPEC REFERENCE: W3C WebDriver §10 - Navigation
//                 https://www.w3.org/TR/webdriver2/#navigation
// =============================================================================

using System;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Navigation commands.
    /// </summary>
    public class NavigationCommands
    {
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
            _handler.GetSession(sessionId);
            
            if (!body.HasValue || !body.Value.TryGetProperty("url", out var urlElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "URL is required");
            }
            
            var url = urlElement.GetString();
            if (string.IsNullOrEmpty(url))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "URL cannot be empty");
            }
            
            // Validate URL format
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"Invalid URL: {url}");
            }
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            
            await _handler.Browser.NavigateAsync(url);
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
    }
}
