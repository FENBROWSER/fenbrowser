using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles alert/prompt/confirm dialog WebDriver commands.
    /// Endpoints: Dismiss, accept, get text, send text
    /// </summary>
    public class AlertCommands : IWebDriverCommand
    {
        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // POST /session/{id}/alert/dismiss
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/alert/dismiss/?$")) return true;
            
            // POST /session/{id}/alert/accept
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/alert/accept/?$")) return true;
            
            // GET /session/{id}/alert/text
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/alert/text/?$")) return true;
            
            // POST /session/{id}/alert/text
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/alert/text/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');

            // POST /session/{id}/alert/dismiss
            if (context.Method == "POST" && path.EndsWith("/alert/dismiss"))
            {
                var hasAlert = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.HasAlertAsync());
                
                if (!hasAlert)
                    return WebDriverResponse.NoAlertOpen("No alert is currently open");

                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.DismissAlertAsync());
                return WebDriverResponse.Success(null);
            }

            // POST /session/{id}/alert/accept
            if (context.Method == "POST" && path.EndsWith("/alert/accept"))
            {
                var hasAlert = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.HasAlertAsync());
                
                if (!hasAlert)
                    return WebDriverResponse.NoAlertOpen("No alert is currently open");

                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.AcceptAlertAsync());
                return WebDriverResponse.Success(null);
            }

            // GET /session/{id}/alert/text
            if (context.Method == "GET" && path.EndsWith("/alert/text"))
            {
                var hasAlert = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.HasAlertAsync());
                
                if (!hasAlert)
                    return WebDriverResponse.NoAlertOpen("No alert is currently open");

                var text = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetAlertTextAsync());
                return WebDriverResponse.Success(text);
            }

            // POST /session/{id}/alert/text
            if (context.Method == "POST" && path.EndsWith("/alert/text"))
            {
                var hasAlert = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.HasAlertAsync());
                
                if (!hasAlert)
                    return WebDriverResponse.NoAlertOpen("No alert is currently open");

                if (!context.Body.TryGetProperty("text", out var textProp))
                    return WebDriverResponse.Error400("Missing 'text' parameter");

                var text = textProp.GetString();
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.SendAlertTextAsync(text));
                return WebDriverResponse.Success(null);
            }

            return WebDriverResponse.Error404("Alert command not found");
        }
    }
}
