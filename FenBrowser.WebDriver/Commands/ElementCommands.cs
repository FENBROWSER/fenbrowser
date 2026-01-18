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
