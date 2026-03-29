using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Routes HTTP requests to command handlers.
    /// </summary>
    public class CommandRouter
    {
        private readonly List<Route> _routes = new();
        private readonly HashSet<string> _registeredCommands = new(StringComparer.Ordinal);
        private readonly HashSet<string> _registeredRouteKeys = new(StringComparer.OrdinalIgnoreCase);

        public CommandRouter()
        {
            RegisterRoutes();
        }

        private void RegisterRoutes()
        {
            AddRoute("GET", "/status", "GetStatus");

            AddRoute("POST", "/session", "NewSession");
            AddRoute("DELETE", "/session/{sessionId}", "DeleteSession");

            AddRoute("GET", "/session/{sessionId}/timeouts", "GetTimeouts");
            AddRoute("POST", "/session/{sessionId}/timeouts", "SetTimeouts");

            AddRoute("POST", "/session/{sessionId}/url", "NavigateTo");
            AddRoute("GET", "/session/{sessionId}/url", "GetCurrentUrl");
            AddRoute("POST", "/session/{sessionId}/back", "Back");
            AddRoute("POST", "/session/{sessionId}/forward", "Forward");
            AddRoute("POST", "/session/{sessionId}/refresh", "Refresh");
            AddRoute("GET", "/session/{sessionId}/title", "GetTitle");

            AddRoute("GET", "/session/{sessionId}/window", "GetWindowHandle");
            AddRoute("DELETE", "/session/{sessionId}/window", "CloseWindow");
            AddRoute("POST", "/session/{sessionId}/window", "SwitchToWindow");
            AddRoute("GET", "/session/{sessionId}/window/handles", "GetWindowHandles");
            AddRoute("POST", "/session/{sessionId}/window/new", "NewWindow");
            AddRoute("POST", "/session/{sessionId}/frame", "SwitchToFrame");
            AddRoute("POST", "/session/{sessionId}/frame/parent", "SwitchToParentFrame");
            AddRoute("GET", "/session/{sessionId}/window/rect", "GetWindowRect");
            AddRoute("POST", "/session/{sessionId}/window/rect", "SetWindowRect");
            AddRoute("POST", "/session/{sessionId}/window/maximize", "MaximizeWindow");
            AddRoute("POST", "/session/{sessionId}/window/minimize", "MinimizeWindow");
            AddRoute("POST", "/session/{sessionId}/window/fullscreen", "FullscreenWindow");

            AddRoute("POST", "/session/{sessionId}/element", "FindElement");
            AddRoute("POST", "/session/{sessionId}/elements", "FindElements");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/element", "FindElementFromElement");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/elements", "FindElementsFromElement");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/shadow", "GetShadowRoot");
            AddRoute("POST", "/session/{sessionId}/shadow/{shadowId}/element", "FindElementFromShadowRoot");
            AddRoute("POST", "/session/{sessionId}/shadow/{shadowId}/elements", "FindElementsFromShadowRoot");
            AddRoute("GET", "/session/{sessionId}/element/active", "GetActiveElement");

            AddRoute("GET", "/session/{sessionId}/element/{elementId}/selected", "IsElementSelected");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/attribute/{name}", "GetElementAttribute");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/property/{name}", "GetElementProperty");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/css/{propertyName}", "GetElementCssValue");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/text", "GetElementText");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/name", "GetElementTagName");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/rect", "GetElementRect");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/enabled", "IsElementEnabled");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/computedrole", "GetComputedRole");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/computedlabel", "GetComputedLabel");

            AddRoute("POST", "/session/{sessionId}/element/{elementId}/click", "ElementClick");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/clear", "ElementClear");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/value", "ElementSendKeys");

            AddRoute("GET", "/session/{sessionId}/source", "GetPageSource");
            AddRoute("POST", "/session/{sessionId}/execute/sync", "ExecuteScript");
            AddRoute("POST", "/session/{sessionId}/execute/async", "ExecuteAsyncScript");

            AddRoute("GET", "/session/{sessionId}/cookie", "GetAllCookies");
            AddRoute("GET", "/session/{sessionId}/cookie/{name}", "GetNamedCookie");
            AddRoute("POST", "/session/{sessionId}/cookie", "AddCookie");
            AddRoute("DELETE", "/session/{sessionId}/cookie/{name}", "DeleteCookie");
            AddRoute("DELETE", "/session/{sessionId}/cookie", "DeleteAllCookies");

            AddRoute("POST", "/session/{sessionId}/actions", "PerformActions");
            AddRoute("DELETE", "/session/{sessionId}/actions", "ReleaseActions");

            AddRoute("POST", "/session/{sessionId}/alert/dismiss", "DismissAlert");
            AddRoute("POST", "/session/{sessionId}/alert/accept", "AcceptAlert");
            AddRoute("GET", "/session/{sessionId}/alert/text", "GetAlertText");
            AddRoute("POST", "/session/{sessionId}/alert/text", "SendAlertText");

            AddRoute("GET", "/session/{sessionId}/screenshot", "TakeScreenshot");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/screenshot", "TakeElementScreenshot");

            AddRoute("POST", "/session/{sessionId}/print", "PrintPage");
        }

        private void AddRoute(string method, string pathTemplate, string command)
        {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("HTTP method is required.", nameof(method));

            if (string.IsNullOrWhiteSpace(pathTemplate))
                throw new ArgumentException("Route path is required.", nameof(pathTemplate));

            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command name is required.", nameof(command));

            var normalizedMethod = method.Trim().ToUpperInvariant();
            var normalizedTemplate = NormalizePath(pathTemplate);
            var key = $"{normalizedMethod} {normalizedTemplate}";
            if (!_registeredRouteKeys.Add(key))
            {
                throw new InvalidOperationException($"Duplicate WebDriver route registration: {key}");
            }

            _routes.Add(new Route(normalizedMethod, normalizedTemplate, command));
            _registeredCommands.Add(command);
        }

        /// <summary>
        /// Match a request to a command.
        /// </summary>
        public RouteMatch Match(string method, string path)
        {
            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
                return null;

            var normalizedMethod = method.Trim().ToUpperInvariant();
            var normalizedPath = NormalizePath(path);

            foreach (var route in _routes)
            {
                var match = route.Match(normalizedMethod, normalizedPath);
                if (match != null)
                    return match;
            }

            return null;
        }

        public IReadOnlyCollection<string> GetRegisteredCommands() => _registeredCommands;
        public int GetRegisteredRouteCount() => _routes.Count;

        private static string NormalizePath(string path)
        {
            var trimmed = path.Trim();
            var queryIndex = trimmed.IndexOf('?');
            if (queryIndex >= 0)
            {
                trimmed = trimmed[..queryIndex];
            }

            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                trimmed = "/" + trimmed;
            }

            if (trimmed.Length > 1)
            {
                trimmed = trimmed.TrimEnd('/');
            }

            return trimmed;
        }
    }

    /// <summary>
    /// A route definition.
    /// </summary>
    public class Route
    {
        public string Method { get; }
        public string PathTemplate { get; }
        public string Command { get; }

        private readonly Regex _pathRegex;
        private readonly List<string> _paramNames = new();

        public Route(string method, string pathTemplate, string command)
        {
            Method = method;
            PathTemplate = pathTemplate;
            Command = command;

            var pattern = "^" + Regex.Replace(pathTemplate, @"\{(\w+)\}", match =>
            {
                _paramNames.Add(match.Groups[1].Value);
                return "([^/]+)";
            }) + "$";

            _pathRegex = new Regex(pattern, RegexOptions.Compiled);
        }

        public RouteMatch Match(string method, string path)
        {
            if (!string.Equals(Method, method, StringComparison.OrdinalIgnoreCase))
                return null;

            var match = _pathRegex.Match(path);
            if (!match.Success)
                return null;

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < _paramNames.Count; i++)
            {
                parameters[_paramNames[i]] = Uri.UnescapeDataString(match.Groups[i + 1].Value);
            }

            return new RouteMatch(Command, parameters);
        }
    }

    /// <summary>
    /// Result of route matching.
    /// </summary>
    public class RouteMatch
    {
        public string Command { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }

        public RouteMatch(string command, Dictionary<string, string> parameters)
        {
            Command = command;
            Parameters = parameters;
        }

        public string GetSessionId() => Parameters.GetValueOrDefault("sessionId");
        public string GetElementId() => Parameters.GetValueOrDefault("elementId");
        public string GetShadowId() => Parameters.GetValueOrDefault("shadowId");
    }
}
