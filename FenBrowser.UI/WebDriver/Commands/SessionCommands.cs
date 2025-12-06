using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles session and status related WebDriver commands.
    /// Endpoints: GET /status, POST /session, DELETE /session/{id}, GET/POST timeouts
    /// </summary>
    public class SessionCommands : IWebDriverCommand
    {
        private readonly WebDriverRouter _router;

        public SessionCommands(WebDriverRouter router)
        {
            _router = router;
        }

        public bool CanHandle(string method, string path)
        {
            // GET /status
            if (method == "GET" && path == "/status") return true;
            
            // POST /session (new session)
            if (method == "POST" && Regex.IsMatch(path, @"^/session/?$")) return true;
            
            // DELETE /session/{id} (delete session)
            if (method == "DELETE" && Regex.IsMatch(path, @"^/session/[^/]+/?$")) return true;
            
            // GET /session/{id}/timeouts
            if (method == "GET" && Regex.IsMatch(path, @"^/session/[^/]+/timeouts/?$")) return true;
            
            // POST /session/{id}/timeouts
            if (method == "POST" && Regex.IsMatch(path, @"^/session/[^/]+/timeouts/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            var path = context.Path.TrimEnd('/');

            // GET /status
            if (context.Method == "GET" && path == "/status")
            {
                return WebDriverResponse.Success(new
                {
                    ready = !_router.HasActiveSessions,
                    message = "FenBrowser WebDriver is ready",
                    os = new
                    {
                        arch = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                        name = Environment.OSVersion.Platform.ToString(),
                        version = Environment.OSVersion.VersionString
                    },
                    build = new
                    {
                        version = "1.0.0"
                    }
                });
            }

            // POST /session (new session)
            if (context.Method == "POST" && Regex.IsMatch(path, @"^/session$"))
            {
                var session = _router.CreateSession();
                return WebDriverResponse.Success(new
                {
                    sessionId = session.SessionId,
                    capabilities = new
                    {
                        browserName = "FenBrowser",
                        browserVersion = "1.0.0",
                        platformName = Environment.OSVersion.Platform.ToString(),
                        acceptInsecureCerts = false,
                        pageLoadStrategy = "normal",
                        timeouts = new
                        {
                            script = session.ScriptTimeout,
                            pageLoad = session.PageLoadTimeout,
                            @implicit = session.ImplicitWaitTimeout
                        }
                    }
                });
            }

            // DELETE /session/{id}
            if (context.Method == "DELETE" && Regex.IsMatch(path, @"^/session/[^/]+$"))
            {
                var sessionId = context.SessionId;
                _router.DeleteSession(sessionId);
                return WebDriverResponse.Success(null);
            }

            // GET /session/{id}/timeouts
            if (context.Method == "GET" && path.EndsWith("/timeouts"))
            {
                if (context.Session == null) return WebDriverResponse.InvalidSession();
                return WebDriverResponse.Success(new
                {
                    script = context.Session.ScriptTimeout,
                    pageLoad = context.Session.PageLoadTimeout,
                    @implicit = context.Session.ImplicitWaitTimeout
                });
            }

            // POST /session/{id}/timeouts
            if (context.Method == "POST" && path.EndsWith("/timeouts"))
            {
                if (context.Session == null) return WebDriverResponse.InvalidSession();

                try
                {
                    if (context.Body.TryGetProperty("script", out var script))
                        context.Session.ScriptTimeout = script.GetInt32();
                    if (context.Body.TryGetProperty("pageLoad", out var pageLoad))
                        context.Session.PageLoadTimeout = pageLoad.GetInt32();
                    if (context.Body.TryGetProperty("implicit", out var implicitWait))
                        context.Session.ImplicitWaitTimeout = implicitWait.GetInt32();
                }
                catch
                {
                    return WebDriverResponse.Error400("Invalid timeout value");
                }

                return WebDriverResponse.Success(null);
            }

            return WebDriverResponse.Error404("Session command not found");
        }
    }
}
