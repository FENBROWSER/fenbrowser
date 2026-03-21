// =============================================================================
// CommandRouter.cs
// W3C WebDriver Command Routing (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §6 - Routing
//                 https://www.w3.org/TR/webdriver2/#processing-model
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Routes HTTP requests to command handlers.
    /// </summary>
    public class CommandRouter
    {
        private readonly List<Route> _routes = new();
        private readonly HashSet<string> _registeredCommands = new(StringComparer.Ordinal);
        
        public CommandRouter()
        {
            RegisterRoutes();
        }
        
        /// <summary>
        /// Register all W3C WebDriver routes.
        /// </summary>
        private void RegisterRoutes()
        {
            // Status
            AddRoute("GET", "/status", "GetStatus");
            
            // Session
            AddRoute("POST", "/session", "NewSession");
            AddRoute("DELETE", "/session/{sessionId}", "DeleteSession");
            
            // Timeouts
            AddRoute("GET", "/session/{sessionId}/timeouts", "GetTimeouts");
            AddRoute("POST", "/session/{sessionId}/timeouts", "SetTimeouts");
            
            // Navigation
            AddRoute("POST", "/session/{sessionId}/url", "NavigateTo");
            AddRoute("GET", "/session/{sessionId}/url", "GetCurrentUrl");
            AddRoute("POST", "/session/{sessionId}/back", "Back");
            AddRoute("POST", "/session/{sessionId}/forward", "Forward");
            AddRoute("POST", "/session/{sessionId}/refresh", "Refresh");
            AddRoute("GET", "/session/{sessionId}/title", "GetTitle");
            
            // Window
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
            
            // Element Finding
            AddRoute("POST", "/session/{sessionId}/element", "FindElement");
            AddRoute("POST", "/session/{sessionId}/elements", "FindElements");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/element", "FindElementFromElement");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/elements", "FindElementsFromElement");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/shadow", "GetShadowRoot");
            AddRoute("POST", "/session/{sessionId}/shadow/{shadowId}/element", "FindElementFromShadowRoot");
            AddRoute("POST", "/session/{sessionId}/shadow/{shadowId}/elements", "FindElementsFromShadowRoot");
            AddRoute("GET", "/session/{sessionId}/element/active", "GetActiveElement");
            
            // Element State
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
            
            // Element Interaction
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/click", "ElementClick");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/clear", "ElementClear");
            AddRoute("POST", "/session/{sessionId}/element/{elementId}/value", "ElementSendKeys");
            
            // Document
            AddRoute("GET", "/session/{sessionId}/source", "GetPageSource");
            AddRoute("POST", "/session/{sessionId}/execute/sync", "ExecuteScript");
            AddRoute("POST", "/session/{sessionId}/execute/async", "ExecuteAsyncScript");
            
            // Cookies
            AddRoute("GET", "/session/{sessionId}/cookie", "GetAllCookies");
            AddRoute("GET", "/session/{sessionId}/cookie/{name}", "GetNamedCookie");
            AddRoute("POST", "/session/{sessionId}/cookie", "AddCookie");
            AddRoute("DELETE", "/session/{sessionId}/cookie/{name}", "DeleteCookie");
            AddRoute("DELETE", "/session/{sessionId}/cookie", "DeleteAllCookies");
            
            // Actions
            AddRoute("POST", "/session/{sessionId}/actions", "PerformActions");
            AddRoute("DELETE", "/session/{sessionId}/actions", "ReleaseActions");
            
            // User prompts
            AddRoute("POST", "/session/{sessionId}/alert/dismiss", "DismissAlert");
            AddRoute("POST", "/session/{sessionId}/alert/accept", "AcceptAlert");
            AddRoute("GET", "/session/{sessionId}/alert/text", "GetAlertText");
            AddRoute("POST", "/session/{sessionId}/alert/text", "SendAlertText");
            
            // Screen capture
            AddRoute("GET", "/session/{sessionId}/screenshot", "TakeScreenshot");
            AddRoute("GET", "/session/{sessionId}/element/{elementId}/screenshot", "TakeElementScreenshot");
            
            // Print
            AddRoute("POST", "/session/{sessionId}/print", "PrintPage");
        }
        
        private void AddRoute(string method, string pathTemplate, string command)
        {
            _routes.Add(new Route(method, pathTemplate, command));
            _registeredCommands.Add(command);
        }
        
        /// <summary>
        /// Match a request to a command.
        /// </summary>
        public RouteMatch Match(string method, string path)
        {
            foreach (var route in _routes)
            {
                var match = route.Match(method, path);
                if (match != null)
                    return match;
            }
            
            return null;
        }

        public IReadOnlyCollection<string> GetRegisteredCommands() => _registeredCommands;
        public int GetRegisteredRouteCount() => _routes.Count;
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
            
            // Convert path template to regex
            var pattern = "^" + Regex.Replace(pathTemplate, @"\{(\w+)\}", m =>
            {
                _paramNames.Add(m.Groups[1].Value);
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
                
            var parameters = new Dictionary<string, string>();
            for (int i = 0; i < _paramNames.Count; i++)
            {
                parameters[_paramNames[i]] = match.Groups[i + 1].Value;
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
