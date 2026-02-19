// =============================================================================
// ElementCommands.cs
// W3C WebDriver Element Commands
// 
// SPEC REFERENCE: W3C WebDriver §12 - Element Retrieval
//                 https://www.w3.org/TR/webdriver2/#element-retrieval
// =============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Element finding and interaction commands.
    /// </summary>
    public class ElementCommands
    {
        private readonly CommandHandler _handler;
        
        // Supported locator strategies per spec
        private static readonly HashSet<string> SupportedStrategies = new()
        {
            "css selector",
            "link text",
            "partial link text",
            "tag name",
            "xpath"
        };
        
        public ElementCommands(CommandHandler handler)
        {
            _handler = handler;
        }
        
        /// <summary>
        /// Find element.
        /// POST /session/{sessionId}/element
        /// </summary>
        public async Task<WebDriverResponse> FindElementAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (strategy, selector) = ParseLocator(body);
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, "No element found");
            }
            
            var element = await _handler.Browser.FindElementAsync(strategy, selector);
            
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, 
                    $"No element found using {strategy}: {selector}");
            }
            
            var elementId = session.RegisterElement(element);
            return WebDriverResponse.Success(new ElementReference(elementId));
        }
        
        /// <summary>
        /// Find multiple elements.
        /// POST /session/{sessionId}/elements
        /// </summary>
        public async Task<WebDriverResponse> FindElementsAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (strategy, selector) = ParseLocator(body);
            
            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success(Array.Empty<ElementReference>());
            }
            
            var elements = await _handler.Browser.FindElementsAsync(strategy, selector);
            
            var refs = new List<ElementReference>();
            foreach (var element in elements ?? Array.Empty<object>())
            {
                var elementId = session.RegisterElement(element);
                refs.Add(new ElementReference(elementId));
            }
            
            return WebDriverResponse.Success(refs);
        }

        public async Task<WebDriverResponse> FindElementFromElementAsync(string sessionId, string parentElementId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var parent = session.GetElement(parentElementId);
            var (strategy, selector) = ParseLocator(body);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, "No element found");
            }

            var element = await _handler.Browser.FindElementAsync(strategy, selector, parent);
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, $"No element found using {strategy}: {selector}");
            }

            var elementId = session.RegisterElement(element);
            return WebDriverResponse.Success(new ElementReference(elementId));
        }

        public async Task<WebDriverResponse> FindElementsFromElementAsync(string sessionId, string parentElementId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var parent = session.GetElement(parentElementId);
            var (strategy, selector) = ParseLocator(body);

            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success(Array.Empty<ElementReference>());
            }

            var elements = await _handler.Browser.FindElementsAsync(strategy, selector, parent);
            var refs = new List<ElementReference>();
            foreach (var element in elements ?? Array.Empty<object>())
            {
                var elementId = session.RegisterElement(element);
                refs.Add(new ElementReference(elementId));
            }

            return WebDriverResponse.Success(refs);
        }

        public async Task<WebDriverResponse> GetActiveElementAsync(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, "Browser not connected");
            }

            var element = await _handler.Browser.GetActiveElementAsync();
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, "No active element");
            }

            var elementId = session.RegisterElement(element);
            return WebDriverResponse.Success(new ElementReference(elementId));
        }
        
        /// <summary>
        /// Get element text.
        /// GET /session/{sessionId}/element/{elementId}/text
        /// </summary>
        public async Task<WebDriverResponse> GetElementTextAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            
            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success("");
            }
            
            var text = await _handler.Browser.GetElementTextAsync(element);
            return WebDriverResponse.Success(text ?? "");
        }
        
        /// <summary>
        /// Click element.
        /// POST /session/{sessionId}/element/{elementId}/click
        /// </summary>
        public async Task<WebDriverResponse> ClickAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.ElementNotInteractable, "Browser not connected");
            }
            
            await _handler.Browser.ClickElementAsync(element);
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Send keys to element.
        /// POST /session/{sessionId}/element/{elementId}/value
        /// </summary>
        public async Task<WebDriverResponse> SendKeysAsync(string sessionId, string elementId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            
            if (!body.HasValue || !body.Value.TryGetProperty("text", out var textElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Text is required");
            }
            
            var text = textElement.GetString() ?? "";
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.ElementNotInteractable, "Browser not connected");
            }
            
            await _handler.Browser.SendKeysAsync(element, text);
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Get element attribute.
        /// GET /session/{sessionId}/element/{elementId}/attribute/{name}
        /// </summary>
        public async Task<WebDriverResponse> GetAttributeAsync(string sessionId, string elementId, string name)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            
            if (string.IsNullOrEmpty(name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Attribute name is required");
            }
            
            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success(null);
            }
            
            var value = await _handler.Browser.GetElementAttributeAsync(element, name);
            return WebDriverResponse.Success(value);
        }

        public async Task<WebDriverResponse> IsSelectedAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null) return WebDriverResponse.Success(false);
            var selected = await _handler.Browser.IsElementSelectedAsync(element);
            return WebDriverResponse.Success(selected);
        }

        public async Task<WebDriverResponse> GetPropertyAsync(string sessionId, string elementId, string name)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Property name is required");
            }
            if (_handler.Browser == null) return WebDriverResponse.Success(null);
            var value = await _handler.Browser.GetElementPropertyAsync(element, name);
            return WebDriverResponse.Success(value);
        }

        public async Task<WebDriverResponse> GetCssValueAsync(string sessionId, string elementId, string propertyName)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "CSS property name is required");
            }
            if (_handler.Browser == null) return WebDriverResponse.Success(string.Empty);
            var value = await _handler.Browser.GetElementCssValueAsync(element, propertyName);
            return WebDriverResponse.Success(value ?? string.Empty);
        }

        public async Task<WebDriverResponse> GetTagNameAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null) return WebDriverResponse.Success(string.Empty);
            var tag = await _handler.Browser.GetElementTagNameAsync(element);
            return WebDriverResponse.Success(tag ?? string.Empty);
        }

        public async Task<WebDriverResponse> GetRectAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null)
            {
                return WebDriverResponse.Success(new WdElementRect());
            }

            var rect = await _handler.Browser.GetElementRectAsync(element);
            return WebDriverResponse.Success(rect ?? new WdElementRect());
        }

        public async Task<WebDriverResponse> IsEnabledAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null) return WebDriverResponse.Success(false);
            var enabled = await _handler.Browser.IsElementEnabledAsync(element);
            return WebDriverResponse.Success(enabled);
        }

        public async Task<WebDriverResponse> GetComputedRoleAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null) return WebDriverResponse.Success(string.Empty);
            var role = await _handler.Browser.GetElementComputedRoleAsync(element);
            return WebDriverResponse.Success(role ?? string.Empty);
        }

        public async Task<WebDriverResponse> GetComputedLabelAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null) return WebDriverResponse.Success(string.Empty);
            var label = await _handler.Browser.GetElementComputedLabelAsync(element);
            return WebDriverResponse.Success(label ?? string.Empty);
        }

        public async Task<WebDriverResponse> ClearAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.ElementNotInteractable, "Browser not connected");
            }

            await _handler.Browser.ClearElementAsync(element);
            return WebDriverResponse.Success(null);
        }

        public async Task<WebDriverResponse> TakeElementScreenshotAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var base64 = await _handler.Browser.TakeElementScreenshotAsync(element);
            return WebDriverResponse.Success(base64 ?? string.Empty);
        }
        
        private (string strategy, string selector) ParseLocator(JsonElement? body)
        {
            if (!body.HasValue)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Locator is required");
            }
            
            if (!body.Value.TryGetProperty("using", out var usingElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Locator strategy is required");
            }
            
            if (!body.Value.TryGetProperty("value", out var valueElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Locator value is required");
            }
            
            var strategy = usingElement.GetString();
            var selector = valueElement.GetString();
            
            if (string.IsNullOrEmpty(strategy) || !SupportedStrategies.Contains(strategy))
            {
                throw new WebDriverException(ErrorCodes.InvalidSelector, 
                    $"Invalid locator strategy: {strategy}");
            }
            
            if (string.IsNullOrEmpty(selector))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Selector cannot be empty");
            }
            
            return (strategy, selector);
        }
    }
}
