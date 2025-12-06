using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Commands;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Routes incoming WebDriver requests to the appropriate command handler.
    /// </summary>
    public class WebDriverRouter
    {
        private readonly List<IWebDriverCommand> _commands = new List<IWebDriverCommand>();
        private readonly Dictionary<string, WebDriverSession> _sessions = new Dictionary<string, WebDriverSession>();
        private readonly IBrowser _browser;

        public WebDriverRouter(IBrowser browser)
        {
            _browser = browser;
            RegisterCommands();
        }

        private void RegisterCommands()
        {
            // Register all command handlers in priority order
            _commands.Add(new SessionCommands(this));
            _commands.Add(new NavigationCommands());
            _commands.Add(new WindowCommands());
            _commands.Add(new ElementCommands());
            _commands.Add(new DocumentCommands());
            _commands.Add(new CookieCommands());
            _commands.Add(new ActionCommands());
            _commands.Add(new AlertCommands());
        }

        /// <summary>
        /// Routes a request to the appropriate handler.
        /// </summary>
        public async Task<WebDriverResponse> RouteAsync(string method, string path, JsonElement body)
        {
            var segments = path.Trim('/').Split('/');
            
            // Build context
            var context = new WebDriverContext
            {
                Method = method,
                Path = path,
                PathSegments = segments,
                Body = body,
                Browser = _browser,
                Session = null
            };

            // Extract session if path contains session ID
            if (segments.Length >= 2 && segments[0] == "session")
            {
                var sessionId = segments[1];
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    context.Session = session;
                }
                else if (method != "POST" || !path.EndsWith("/session"))
                {
                    // Session required but not found (except for new session)
                    return WebDriverResponse.InvalidSession();
                }
            }

            // Find and execute matching handler
            foreach (var command in _commands)
            {
                if (command.CanHandle(method, path))
                {
                    return await command.ExecuteAsync(context);
                }
            }

            return WebDriverResponse.Error404($"Unknown command: {method} {path}");
        }

        #region Session Management

        public WebDriverSession CreateSession()
        {
            var session = new WebDriverSession();
            _sessions[session.SessionId] = session;
            return session;
        }

        public bool DeleteSession(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.Close();
                return _sessions.Remove(sessionId);
            }
            return false;
        }

        public WebDriverSession GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        public IEnumerable<WebDriverSession> GetAllSessions()
        {
            return _sessions.Values;
        }

        public bool HasActiveSessions => _sessions.Count > 0;

        #endregion
    }
}
