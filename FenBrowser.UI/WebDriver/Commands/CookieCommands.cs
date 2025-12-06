using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles cookie management WebDriver commands.
    /// Endpoints: Get/Add/Delete cookies
    /// </summary>
    public class CookieCommands : IWebDriverCommand
    {
        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // GET /session/{id}/cookie - Get all cookies
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/cookie/?$")) return true;
            
            // GET /session/{id}/cookie/{name} - Get named cookie
            if (method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/cookie/[^/]+/?$")) return true;
            
            // POST /session/{id}/cookie - Add cookie
            if (method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/cookie/?$")) return true;
            
            // DELETE /session/{id}/cookie/{name} - Delete named cookie
            if (method == "DELETE" && Regex.IsMatch(path, @"/session/[^/]+/cookie/[^/]+/?$")) return true;
            
            // DELETE /session/{id}/cookie - Delete all cookies
            if (method == "DELETE" && Regex.IsMatch(path, @"/session/[^/]+/cookie/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');
            var segments = context.PathSegments;

            // GET /session/{id}/cookie - Get all cookies
            if (context.Method == "GET" && Regex.IsMatch(path, @"/session/[^/]+/cookie$"))
            {
                var cookies = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetAllCookiesAsync());
                return WebDriverResponse.Success(cookies);
            }

            // GET /session/{id}/cookie/{name} - Get named cookie
            var cookieNameMatch = Regex.Match(path, @"/session/[^/]+/cookie/([^/]+)$");
            if (context.Method == "GET" && cookieNameMatch.Success)
            {
                var name = cookieNameMatch.Groups[1].Value;
                var cookie = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetCookieAsync(name));
                
                if (cookie == null)
                    return WebDriverResponse.NoSuchCookie($"Cookie '{name}' not found");
                
                return WebDriverResponse.Success(cookie);
            }

            // POST /session/{id}/cookie - Add cookie
            if (context.Method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/cookie$"))
            {
                if (!context.Body.TryGetProperty("cookie", out var cookieProp))
                    return WebDriverResponse.Error400("Missing 'cookie' parameter");

                var cookie = new WebDriverCookie();
                
                if (cookieProp.TryGetProperty("name", out var nameProp))
                    cookie.Name = nameProp.GetString();
                if (cookieProp.TryGetProperty("value", out var valueProp))
                    cookie.Value = valueProp.GetString();
                if (cookieProp.TryGetProperty("path", out var pathProp))
                    cookie.Path = pathProp.GetString();
                if (cookieProp.TryGetProperty("domain", out var domainProp))
                    cookie.Domain = domainProp.GetString();
                if (cookieProp.TryGetProperty("secure", out var secureProp))
                    cookie.Secure = secureProp.GetBoolean();
                if (cookieProp.TryGetProperty("httpOnly", out var httpOnlyProp))
                    cookie.HttpOnly = httpOnlyProp.GetBoolean();
                if (cookieProp.TryGetProperty("expiry", out var expiryProp))
                    cookie.Expiry = expiryProp.GetInt64();
                if (cookieProp.TryGetProperty("sameSite", out var sameSiteProp))
                    cookie.SameSite = sameSiteProp.GetString();

                if (string.IsNullOrEmpty(cookie.Name))
                    return WebDriverResponse.Error400("Cookie name is required");

                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.AddCookieAsync(cookie));
                
                return WebDriverResponse.Success(null);
            }

            // DELETE /session/{id}/cookie/{name} - Delete named cookie
            if (context.Method == "DELETE" && cookieNameMatch.Success)
            {
                var name = cookieNameMatch.Groups[1].Value;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.DeleteCookieAsync(name));
                return WebDriverResponse.Success(null);
            }

            // DELETE /session/{id}/cookie - Delete all cookies
            if (context.Method == "DELETE" && Regex.IsMatch(path, @"/session/[^/]+/cookie$"))
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.DeleteAllCookiesAsync());
                return WebDriverResponse.Success(null);
            }

            return WebDriverResponse.Error404("Cookie command not found");
        }
    }
}
