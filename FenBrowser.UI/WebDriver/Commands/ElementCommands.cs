using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Handles element retrieval, state, and interaction WebDriver commands.
    /// Endpoints: Find elements, element state, click, clear, send keys
    /// </summary>
    public class ElementCommands : IWebDriverCommand
    {
        private const string ELEMENT_KEY = "element-6066-11e4-a52e-4f735466cecf";
        private const string SHADOW_KEY = "shadow-6066-11e4-a52e-4f735466cecf";

        public bool CanHandle(string method, string path)
        {
            if (!path.Contains("/session/")) return false;

            // Find element(s)
            if (Regex.IsMatch(path, @"/session/[^/]+/element/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/elements/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/active/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/element/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/elements/?$")) return true;

            // Shadow DOM
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/shadow/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/shadow/[^/]+/element/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/shadow/[^/]+/elements/?$")) return true;

            // Element state
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/selected/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/attribute/[^/]+/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/property/[^/]+/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/css/[^/]+/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/text/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/name/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/rect/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/enabled/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/computedrole/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/computedlabel/?$")) return true;

            // Element interaction
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/click/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/clear/?$")) return true;
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/value/?$")) return true;

            // Element screenshot
            if (Regex.IsMatch(path, @"/session/[^/]+/element/[^/]+/screenshot/?$")) return true;

            return false;
        }

        public async Task<WebDriverResponse> ExecuteAsync(WebDriverContext context)
        {
            if (context.Session == null) return WebDriverResponse.InvalidSession();

            var path = context.Path.TrimEnd('/');
            var segments = context.PathSegments;

            // POST /session/{id}/element - Find single element
            if (context.Method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/element$"))
            {
                return await FindElementAsync(context, null);
            }

            // POST /session/{id}/elements - Find multiple elements
            if (context.Method == "POST" && Regex.IsMatch(path, @"/session/[^/]+/elements$"))
            {
                return await FindElementsAsync(context, null);
            }

            // GET /session/{id}/element/active - Get active element
            if (context.Method == "GET" && path.EndsWith("/element/active"))
            {
                var elementId = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetActiveElementAsync());
                if (string.IsNullOrEmpty(elementId))
                    return WebDriverResponse.NoSuchElement("No active element");
                return WebDriverResponse.Success(new Dictionary<string, string> { { ELEMENT_KEY, elementId } });
            }

            // POST /session/{id}/element/{id}/element - Find element from element
            if (context.Method == "POST" && Regex.IsMatch(path, @"/element/[^/]+/element$"))
            {
                var parentId = context.ElementId;
                return await FindElementAsync(context, parentId);
            }

            // POST /session/{id}/element/{id}/elements - Find elements from element
            if (context.Method == "POST" && Regex.IsMatch(path, @"/element/[^/]+/elements$"))
            {
                var parentId = context.ElementId;
                return await FindElementsAsync(context, parentId);
            }

            // GET /session/{id}/element/{id}/shadow - Get shadow root
            if (context.Method == "GET" && path.EndsWith("/shadow"))
            {
                var elementId = context.ElementId;
                var shadowId = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await context.Browser.GetShadowRootAsync(elementId));
                if (string.IsNullOrEmpty(shadowId))
                    return WebDriverResponse.NoSuchElement("No shadow root");
                return WebDriverResponse.Success(new Dictionary<string, string> { { SHADOW_KEY, shadowId } });
            }

            // Element state getters
            if (context.Method == "GET")
            {
                var elementId = context.ElementId;

                // GET /session/{id}/element/{id}/selected
                if (path.EndsWith("/selected"))
                {
                    var selected = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.IsElementSelectedAsync(elementId));
                    return WebDriverResponse.Success(selected);
                }

                // GET /session/{id}/element/{id}/attribute/{name}
                var attrMatch = Regex.Match(path, @"/element/[^/]+/attribute/([^/]+)$");
                if (attrMatch.Success)
                {
                    var attrName = attrMatch.Groups[1].Value;
                    var value = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementAttributeAsync(elementId, attrName));
                    return WebDriverResponse.Success(value);
                }

                // GET /session/{id}/element/{id}/property/{name}
                var propMatch = Regex.Match(path, @"/element/[^/]+/property/([^/]+)$");
                if (propMatch.Success)
                {
                    var propName = propMatch.Groups[1].Value;
                    var value = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementPropertyAsync(elementId, propName));
                    return WebDriverResponse.Success(value);
                }

                // GET /session/{id}/element/{id}/css/{property}
                var cssMatch = Regex.Match(path, @"/element/[^/]+/css/([^/]+)$");
                if (cssMatch.Success)
                {
                    var cssProperty = cssMatch.Groups[1].Value;
                    var value = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementCssValueAsync(elementId, cssProperty));
                    return WebDriverResponse.Success(value);
                }

                // GET /session/{id}/element/{id}/text
                if (path.EndsWith("/text"))
                {
                    var text = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementTextAsync(elementId));
                    return WebDriverResponse.Success(text);
                }

                // GET /session/{id}/element/{id}/name
                if (path.EndsWith("/name"))
                {
                    var name = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementTagNameAsync(elementId));
                    return WebDriverResponse.Success(name);
                }

                // GET /session/{id}/element/{id}/rect
                if (path.EndsWith("/rect"))
                {
                    var rect = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementRectAsync(elementId));
                    return WebDriverResponse.Success(rect);
                }

                // GET /session/{id}/element/{id}/enabled
                if (path.EndsWith("/enabled"))
                {
                    var enabled = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.IsElementEnabledAsync(elementId));
                    return WebDriverResponse.Success(enabled);
                }

                // GET /session/{id}/element/{id}/computedrole
                if (path.EndsWith("/computedrole"))
                {
                    var role = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementComputedRoleAsync(elementId));
                    return WebDriverResponse.Success(role);
                }

                // GET /session/{id}/element/{id}/computedlabel
                if (path.EndsWith("/computedlabel"))
                {
                    var label = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.GetElementComputedLabelAsync(elementId));
                    return WebDriverResponse.Success(label);
                }

                // GET /session/{id}/element/{id}/screenshot
                if (path.EndsWith("/screenshot"))
                {
                    var elementId2 = context.ElementId;
                    var base64 = await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.CaptureElementScreenshotAsync(elementId2));
                    return WebDriverResponse.Success(base64);
                }
            }

            // Element interactions
            if (context.Method == "POST")
            {
                var elementId = context.ElementId;

                // POST /session/{id}/element/{id}/click
                if (path.EndsWith("/click"))
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.ClickElementAsync(elementId));
                    return WebDriverResponse.Success(null);
                }

                // POST /session/{id}/element/{id}/clear
                if (path.EndsWith("/clear"))
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.ClearElementAsync(elementId));
                    return WebDriverResponse.Success(null);
                }

                // POST /session/{id}/element/{id}/value - Send keys
                if (path.EndsWith("/value"))
                {
                    if (!context.Body.TryGetProperty("text", out var textProp))
                        return WebDriverResponse.Error400("Missing 'text' parameter");

                    var text = textProp.GetString();
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                        await context.Browser.SendKeysToElementAsync(elementId, text));
                    return WebDriverResponse.Success(null);
                }
            }

            return WebDriverResponse.Error404("Element command not found");
        }

        private async Task<WebDriverResponse> FindElementAsync(WebDriverContext context, string parentId)
        {
            if (!context.Body.TryGetProperty("using", out var usingProp))
                return WebDriverResponse.Error400("Missing 'using' parameter");
            if (!context.Body.TryGetProperty("value", out var valueProp))
                return WebDriverResponse.Error400("Missing 'value' parameter");

            var strategy = usingProp.GetString();
            var selector = valueProp.GetString();

            var elementId = await Dispatcher.UIThread.InvokeAsync(async () =>
                await context.Browser.FindElementAsync(strategy, selector, parentId));

            if (string.IsNullOrEmpty(elementId))
                return WebDriverResponse.NoSuchElement($"Element not found: {strategy}={selector}");

            return WebDriverResponse.Success(new Dictionary<string, string> { { ELEMENT_KEY, elementId } });
        }

        private async Task<WebDriverResponse> FindElementsAsync(WebDriverContext context, string parentId)
        {
            if (!context.Body.TryGetProperty("using", out var usingProp))
                return WebDriverResponse.Error400("Missing 'using' parameter");
            if (!context.Body.TryGetProperty("value", out var valueProp))
                return WebDriverResponse.Error400("Missing 'value' parameter");

            var strategy = usingProp.GetString();
            var selector = valueProp.GetString();

            var elementIds = await Dispatcher.UIThread.InvokeAsync(async () =>
                await context.Browser.FindElementsAsync(strategy, selector, parentId));

            var results = new List<Dictionary<string, string>>();
            foreach (var id in elementIds)
            {
                results.Add(new Dictionary<string, string> { { ELEMENT_KEY, id } });
            }

            return WebDriverResponse.Success(results);
        }
    }
}
