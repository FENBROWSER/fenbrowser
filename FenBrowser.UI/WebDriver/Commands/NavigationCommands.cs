using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles navigation related WebDriver commands.
    /// Endpoints: GET/POST url, back, forward, refresh
    /// </summary>
    public class NavigationCommands : IWebDriverCommand
    {
        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // GET /session/{id}/url
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/url/?$")) return true;
            
            // POST /session/{id}/url
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/url/?$")) return true;
            
            // POST /session/{id}/back
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/back/?$")) return true;
            
            // POST /session/{id}/forward
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/forward/?$")) return true;
            
            // POST /session/{id}/refresh
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/refresh/?$")) return true;
            
            // GET /session/{id}/title
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/title/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');

            // GET /session/{id}/url
            if (context.Method == "GET" && path.EndsWith("/url"))
            {
                var url = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetCurrentUrlAsync());
                return WebDriverResponse.Success(url);
            }

            // POST /session/{id}/url
            if (context.Method == "POST" && path.EndsWith("/url"))
            {
                if (!context.Body.TryGetProperty("url", out var urlProp))
                    return WebDriverResponse.Error400("Missing 'url' parameter");

                var url = urlProp.GetString();
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.NavigateAsync(url));
                return WebDriverResponse.Success(null);
            }

            // POST /session/{id}/back
            if (context.Method == "POST" && path.EndsWith("/back"))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GoBackAsync());
                return WebDriverResponse.Success(null);
            }

            // POST /session/{id}/forward
            if (context.Method == "POST" && path.EndsWith("/forward"))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GoForwardAsync());
                return WebDriverResponse.Success(null);
            }

            // POST /session/{id}/refresh
            if (context.Method == "POST" && path.EndsWith("/refresh"))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.RefreshAsync());
                return WebDriverResponse.Success(null);
            }

            // GET /session/{id}/title
            if (context.Method == "GET" && path.EndsWith("/title"))
            {
                var title = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetTitleAsync());
                return WebDriverResponse.Success(title);
            }

            return WebDriverResponse.Error404("Navigation command not found");
        }
    }
}
