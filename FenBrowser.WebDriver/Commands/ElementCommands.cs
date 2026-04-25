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
        private const string WebDriverElementTokenPrefix = "__fen_wd_el__:";
        private const string WebDriverShadowTokenPrefix = "__fen_wd_sr__:";
        private const string WebDriverFrameTokenPrefix = "__fen_wd_fr__:";
        private const string WebDriverWindowTokenPrefix = "__fen_wd_win__:";
        private const int NullImplicitWaitFallbackMs = 0;
        private static readonly TimeSpan FindPollInterval = TimeSpan.FromMilliseconds(50);
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
            
            var element = await FindElementWithImplicitWaitAsync(
                session,
                () => _handler.Browser.FindElementAsync(strategy, selector));
            
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, 
                    $"No element found using {strategy}: {selector}");
            }
            
            var elementId = await RegisterElementReferenceAsync(session, element);
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
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            
            var elements = await FindElementsWithImplicitWaitAsync(
                session,
                () => _handler.Browser.FindElementsAsync(strategy, selector));
            
            var refs = new List<ElementReference>();
            foreach (var element in elements ?? Array.Empty<object>())
            {
                var elementId = await RegisterElementReferenceAsync(session, element);
                refs.Add(new ElementReference(elementId));
            }
            
            return WebDriverResponse.Success(refs);
        }

        public async Task<WebDriverResponse> FindElementFromElementAsync(string sessionId, string parentElementId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (strategy, selector) = ParseLocator(body);
            var parent = session.GetElement(parentElementId, Session.ElementReferenceKind.Element);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, "No element found");
            }

            var element = await FindElementWithImplicitWaitAsync(
                session,
                () => _handler.Browser.FindElementAsync(strategy, selector, parent));
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, $"No element found using {strategy}: {selector}");
            }

            var elementId = await RegisterElementReferenceAsync(session, element);
            return WebDriverResponse.Success(new ElementReference(elementId));
        }

        public async Task<WebDriverResponse> FindElementsFromElementAsync(string sessionId, string parentElementId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (strategy, selector) = ParseLocator(body);
            var parent = session.GetElement(parentElementId, Session.ElementReferenceKind.Element);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var elements = await FindElementsWithImplicitWaitAsync(
                session,
                () => _handler.Browser.FindElementsAsync(strategy, selector, parent));
            var refs = new List<ElementReference>();
            foreach (var element in elements ?? Array.Empty<object>())
            {
                var elementId = await RegisterElementReferenceAsync(session, element);
                refs.Add(new ElementReference(elementId));
            }

            return WebDriverResponse.Success(refs);
        }

        public async Task<WebDriverResponse> GetShadowRootAsync(string sessionId, string elementId)
        {
            if (_handler.Browser != null && !_handler.Browser.HasValidCurrentBrowsingContext())
            {
                throw new WebDriverException(ErrorCodes.NoSuchWindow, "Current browsing context is no longer open");
            }

            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchShadowRoot, "Browser not connected");
            }

            var shadowRoot = await _handler.Browser.GetShadowRootAsync(element);
            if (shadowRoot == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchShadowRoot, "Element does not have an open shadow root");
            }

            var shadowId = session.RegisterShadowRoot(shadowRoot);
            return WebDriverResponse.Success(new ShadowRootReference(shadowId));
        }

        public async Task<WebDriverResponse> FindElementFromShadowRootAsync(string sessionId, string shadowId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (strategy, selector) = ParseLocator(body);
            var shadowRoot = session.GetElement(shadowId, Session.ElementReferenceKind.ShadowRoot);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, "No element found");
            }

            var element = await FindElementWithImplicitWaitAsync(
                session,
                () => _handler.Browser.FindElementAsync(strategy, selector, shadowRoot));
            if (element == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, $"No element found using {strategy}: {selector}");
            }

            var elementId = await RegisterElementReferenceAsync(session, element);
            return WebDriverResponse.Success(new ElementReference(elementId));
        }

        public async Task<WebDriverResponse> FindElementsFromShadowRootAsync(string sessionId, string shadowId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (strategy, selector) = ParseLocator(body);
            var shadowRoot = session.GetElement(shadowId, Session.ElementReferenceKind.ShadowRoot);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var elements = await FindElementsWithImplicitWaitAsync(
                session,
                () => _handler.Browser.FindElementsAsync(strategy, selector, shadowRoot));
            var refs = new List<ElementReference>();
            foreach (var element in elements ?? Array.Empty<object>())
            {
                var elementId = await RegisterElementReferenceAsync(session, element);
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

            var elementId = await RegisterElementReferenceAsync(session, element);
            return WebDriverResponse.Success(new ElementReference(elementId));
        }
        
        /// <summary>
        /// Get element text.
        /// GET /session/{sessionId}/element/{elementId}/text
        /// </summary>
        public async Task<WebDriverResponse> GetElementTextAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
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
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            
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
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            
            if (!body.HasValue || !body.Value.TryGetProperty("text", out var textElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Text is required");
            }

            if (textElement.ValueKind != JsonValueKind.String)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Text must be a string");
            }

            var text = textElement.GetString() ?? string.Empty;
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.ElementNotInteractable, "Browser not connected");
            }

            await _handler.Browser.SendKeysAsync(element, text, session.Capabilities?.StrictFileInteractability == true);
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Get element attribute.
        /// GET /session/{sessionId}/element/{elementId}/attribute/{name}
        /// </summary>
        public async Task<WebDriverResponse> GetAttributeAsync(string sessionId, string elementId, string name)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            
            if (string.IsNullOrEmpty(name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Attribute name is required");
            }
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            
            var value = await _handler.Browser.GetElementAttributeAsync(element, name);
            return WebDriverResponse.Success(value);
        }

        public async Task<WebDriverResponse> IsSelectedAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var selected = await _handler.Browser.IsElementSelectedAsync(element);
            return WebDriverResponse.Success(selected);
        }

        public async Task<WebDriverResponse> GetPropertyAsync(string sessionId, string elementId, string name)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Property name is required");
            }
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var value = await _handler.Browser.GetElementPropertyAsync(element, name);
            return WebDriverResponse.Success(SerializePropertyValue(value, session));
        }

        private static object SerializePropertyValue(object value, Session session)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string elementToken &&
                elementToken.StartsWith(WebDriverElementTokenPrefix, StringComparison.Ordinal))
            {
                var nativeElementId = elementToken.Substring(WebDriverElementTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeElementId, out var existingRef))
                {
                    return new ElementReference(existingRef);
                }

                return new ElementReference(session.RegisterElement(nativeElementId));
            }

            if (value is string shadowToken &&
                shadowToken.StartsWith(WebDriverShadowTokenPrefix, StringComparison.Ordinal))
            {
                var nativeShadowId = shadowToken.Substring(WebDriverShadowTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeShadowId, out var existingShadowRef))
                {
                    return new ShadowRootReference(existingShadowRef);
                }

                return new ShadowRootReference(session.RegisterShadowRoot(nativeShadowId));
            }

            if (value is string frameToken &&
                frameToken.StartsWith(WebDriverFrameTokenPrefix, StringComparison.Ordinal))
            {
                var nativeFrameId = frameToken.Substring(WebDriverFrameTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeFrameId, out var existingFrameRef))
                {
                    return new FrameReference(existingFrameRef);
                }

                return new FrameReference(session.RegisterFrame(nativeFrameId));
            }

            if (value is string windowToken &&
                windowToken.StartsWith(WebDriverWindowTokenPrefix, StringComparison.Ordinal))
            {
                var nativeWindowId = windowToken.Substring(WebDriverWindowTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeWindowId, out var existingWindowRef))
                {
                    return new WindowReference(existingWindowRef);
                }

                return new WindowReference(session.RegisterWindow(nativeWindowId));
            }

            if (value is IReadOnlyList<object> readonlyList)
            {
                var serializedList = new List<object>(readonlyList.Count);
                foreach (var item in readonlyList)
                {
                    serializedList.Add(SerializePropertyValue(item, session));
                }
                return serializedList;
            }

            if (value is IEnumerable<object> objectEnumerable)
            {
                var serializedList = new List<object>();
                foreach (var item in objectEnumerable)
                {
                    serializedList.Add(SerializePropertyValue(item, session));
                }
                return serializedList;
            }

            if (value is IDictionary<string, object> dict)
            {
                var serialized = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var entry in dict)
                {
                    serialized[entry.Key] = SerializePropertyValue(entry.Value, session);
                }
                return serialized;
            }

            return value;
        }

        public async Task<WebDriverResponse> GetCssValueAsync(string sessionId, string elementId, string propertyName)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "CSS property name is required");
            }
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var value = await _handler.Browser.GetElementCssValueAsync(element, propertyName);
            return WebDriverResponse.Success(value ?? string.Empty);
        }

        public async Task<WebDriverResponse> GetTagNameAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var tag = await _handler.Browser.GetElementTagNameAsync(element);
            return WebDriverResponse.Success(tag ?? string.Empty);
        }

        public async Task<WebDriverResponse> GetRectAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var rect = await _handler.Browser.GetElementRectAsync(element);
            return WebDriverResponse.Success(rect ?? new WdElementRect());
        }

        public async Task<WebDriverResponse> IsEnabledAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var enabled = await _handler.Browser.IsElementEnabledAsync(element);
            return WebDriverResponse.Success(enabled);
        }

        public async Task<WebDriverResponse> GetComputedRoleAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var role = await _handler.Browser.GetElementComputedRoleAsync(element);
            return WebDriverResponse.Success(role ?? string.Empty);
        }

        public async Task<WebDriverResponse> GetComputedLabelAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null) throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            var label = await _handler.Browser.GetElementComputedLabelAsync(element);
            return WebDriverResponse.Success(label ?? string.Empty);
        }

        public async Task<WebDriverResponse> ClearAsync(string sessionId, string elementId)
        {
            var session = _handler.GetSession(sessionId);
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
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
            var element = session.GetElement(elementId, Session.ElementReferenceKind.Element);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var base64 = await _handler.Browser.TakeElementScreenshotAsync(element);
            return WebDriverResponse.Success(base64 ?? string.Empty);
        }
        
        private (string strategy, string selector) ParseLocator(JsonElement? body)
        {
            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
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
            
            if (usingElement.ValueKind != JsonValueKind.String)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Locator strategy must be a string");
            }

            if (valueElement.ValueKind != JsonValueKind.String)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Locator value must be a string");
            }

            var strategy = usingElement.GetString();
            var selector = valueElement.GetString();
            
            if (string.IsNullOrEmpty(strategy) || !SupportedStrategies.Contains(strategy))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument,
                    $"Invalid locator strategy: {strategy}");
            }
            
            if (string.IsNullOrEmpty(selector))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Selector cannot be empty");
            }
            
            return (strategy, selector);
        }

        private async Task<string> RegisterElementReferenceAsync(Session session, object element)
        {
            if (element is not string nativeId || string.IsNullOrWhiteSpace(nativeId))
            {
                return session.RegisterElement(element);
            }

            if (session.TryGetElementReferenceId(nativeId, out var existingRef))
            {
                return existingRef;
            }

            var equivalentRef = await TryFindEquivalentElementReferenceAsync(session, nativeId);
            if (!string.IsNullOrWhiteSpace(equivalentRef))
            {
                session.AssociateNativeReference(nativeId, equivalentRef);
                return equivalentRef;
            }

            return session.RegisterElement(nativeId);
        }

        private async Task<string> TryFindEquivalentElementReferenceAsync(Session session, string nativeId)
        {
            if (_handler.Browser == null)
            {
                return null;
            }

            var target = await BuildFingerprintAsync(nativeId);
            if (!target.IsValid)
            {
                return null;
            }

            var snapshot = session.GetNativeReferenceMapSnapshot();
            foreach (var kvp in snapshot)
            {
                var candidateNativeId = kvp.Key;
                var candidateRef = kvp.Value;
                if (string.Equals(candidateNativeId, nativeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = await BuildFingerprintAsync(candidateNativeId);
                if (!candidate.IsValid)
                {
                    continue;
                }

                if (target.Equals(candidate))
                {
                    try
                    {
                        _ = session.GetElement(candidateRef, Session.ElementReferenceKind.Element);
                    }
                    catch
                    {
                        continue;
                    }

                    return candidateRef;
                }
            }

            return null;
        }

        private async Task<ElementFingerprint> BuildFingerprintAsync(string nativeId)
        {
            try
            {
                var tag = await _handler.Browser.GetElementTagNameAsync(nativeId) ?? string.Empty;
                var text = await _handler.Browser.GetElementTextAsync(nativeId) ?? string.Empty;
                var idAttr = await _handler.Browser.GetElementAttributeAsync(nativeId, "id") ?? string.Empty;
                var hrefAttr = await _handler.Browser.GetElementAttributeAsync(nativeId, "href") ?? string.Empty;
                return new ElementFingerprint(tag, text, idAttr, hrefAttr);
            }
            catch
            {
                return ElementFingerprint.Invalid;
            }
        }

        private readonly struct ElementFingerprint
        {
            public static ElementFingerprint Invalid => new ElementFingerprint(string.Empty, string.Empty, string.Empty, string.Empty);

            public ElementFingerprint(string tag, string text, string idAttr, string hrefAttr)
            {
                Tag = tag ?? string.Empty;
                Text = text ?? string.Empty;
                IdAttr = idAttr ?? string.Empty;
                HrefAttr = hrefAttr ?? string.Empty;
            }

            public string Tag { get; }
            public string Text { get; }
            public string IdAttr { get; }
            public string HrefAttr { get; }
            public bool IsValid => !string.IsNullOrWhiteSpace(Tag);

            public bool Equals(ElementFingerprint other)
            {
                return string.Equals(Tag, other.Tag, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(Text, other.Text, StringComparison.Ordinal) &&
                       string.Equals(IdAttr, other.IdAttr, StringComparison.Ordinal) &&
                       string.Equals(HrefAttr, other.HrefAttr, StringComparison.Ordinal);
            }
        }

        private async Task<object> FindElementWithImplicitWaitAsync(
            Session session,
            Func<Task<object>> findOperation)
        {
            var timeoutMs = ResolveImplicitTimeoutMs(session);
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (true)
            {
                var element = await findOperation();
                if (element != null)
                {
                    return element;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return null;
                }

                await Task.Delay(FindPollInterval).ConfigureAwait(false);
            }
        }

        private async Task<object[]> FindElementsWithImplicitWaitAsync(
            Session session,
            Func<Task<object[]>> findOperation)
        {
            var timeoutMs = ResolveImplicitTimeoutMs(session);
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (true)
            {
                var elements = await findOperation();
                if (elements != null && elements.Length > 0)
                {
                    return elements;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    return Array.Empty<object>();
                }

                await Task.Delay(FindPollInterval).ConfigureAwait(false);
            }
        }

        private static int ResolveImplicitTimeoutMs(Session session)
        {
            if (session?.Timeouts == null)
            {
                return 0;
            }

            if (session.Timeouts.Implicit.HasValue)
            {
                return Math.Max(0, session.Timeouts.Implicit.Value);
            }

            return NullImplicitWaitFallbackMs;
        }
    }
}

