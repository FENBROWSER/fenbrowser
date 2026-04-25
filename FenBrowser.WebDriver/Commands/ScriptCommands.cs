using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Script execution commands.
    /// </summary>
    public class ScriptCommands
    {
        private const string WebDriverElementTokenPrefix = "__fen_wd_el__:";
        private const string WebDriverShadowTokenPrefix = "__fen_wd_sr__:";
        private const string WebDriverFrameTokenPrefix = "__fen_wd_fr__:";
        private const string WebDriverWindowTokenPrefix = "__fen_wd_win__:";
        private readonly CommandHandler _handler;

        public ScriptCommands(CommandHandler handler)
        {
            _handler = handler;
        }

        /// <summary>
        /// Execute synchronous script.
        /// POST /session/{sessionId}/execute/sync
        /// </summary>
        public async Task<WebDriverResponse> ExecuteSyncAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (script, args) = ParseScriptRequest(body, session);

            _handler.EnsureScriptAllowed(sessionId, script);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, "Browser not connected");
            }

            var timeout = session.Timeouts.Script ?? 30000;
            var syncExecutionWrapper = @"
                var __wdCallback = arguments[arguments.length - 1];
                var __wdAllArgs = Array.prototype.slice.call(arguments, 0, arguments.length - 1);
                var __wdScriptBody = String(__wdAllArgs.shift() || '');
                var __wdArgs = __wdAllArgs;
                try {
                    var __wdRunner = eval('(async function(arguments){' + __wdScriptBody + '})');
                    var __wdResult = __wdRunner.call(window, __wdArgs);
                    if (__wdResult && typeof __wdResult.then === 'function') {
                        __wdResult.then(function(__wdValue) {
                            __wdCallback(__wdValue);
                        }, function(__wdError) {
                            __wdCallback(Promise.reject(__wdError));
                        });
                    } else {
                        __wdCallback(__wdResult);
                    }
                } catch (__wdError) {
                    __wdCallback(Promise.reject(__wdError));
                }";
            var argsWithScript = new object[(args?.Length ?? 0) + 1];
            argsWithScript[0] = script;
            if (args != null && args.Length > 0)
            {
                Array.Copy(args, 0, argsWithScript, 1, args.Length);
            }

            try
            {
                var result = await _handler.Browser.ExecuteAsyncScriptAsync(syncExecutionWrapper, argsWithScript, timeout);
                return WebDriverResponse.Success(SerializeResult(result, session));
            }
            catch (TimeoutException)
            {
                throw new WebDriverException(ErrorCodes.ScriptTimeout, "Script execution timed out");
            }
            catch (WebDriverException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ShouldSurfaceAsProtocolStateError(ex.Message))
            {
                // Preserve host/runtime invalid-operation signals so CommandHandler can map
                // to specific WebDriver protocol errors (no such element, stale, etc).
                throw;
            }
            catch (InvalidOperationException ex)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, ex.Message);
            }
            catch (Exception ex)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, ex.Message);
            }
        }

        /// <summary>
        /// Execute asynchronous script.
        /// POST /session/{sessionId}/execute/async
        /// </summary>
        public async Task<WebDriverResponse> ExecuteAsyncAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var (script, args) = ParseScriptRequest(body, session);

            _handler.EnsureScriptAllowed(sessionId, script);

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, "Browser not connected");
            }

            var timeout = session.Timeouts.Script ?? 30000;

            try
            {
                var result = await _handler.Browser.ExecuteAsyncScriptAsync(script, args, timeout);
                return WebDriverResponse.Success(SerializeResult(result, session));
            }
            catch (TimeoutException)
            {
                throw new WebDriverException(ErrorCodes.ScriptTimeout, "Script execution timed out");
            }
            catch (WebDriverException)
            {
                throw;
            }
            catch (InvalidOperationException ex) when (ShouldSurfaceAsProtocolStateError(ex.Message))
            {
                // Preserve host/runtime invalid-operation signals so CommandHandler can map
                // to specific WebDriver protocol errors (no such element, stale, etc).
                throw;
            }
            catch (InvalidOperationException ex)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, ex.Message);
            }
            catch (Exception ex)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, ex.Message);
            }
        }

        private static bool ShouldSurfaceAsProtocolStateError(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.IndexOf("no such window", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("browsing context is no longer open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("no such frame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("no such shadow root", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("detached shadow root", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("stale element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("no such element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("invalid selector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("element not interactable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("invalid element state", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private (string script, object[] args) ParseScriptRequest(JsonElement? body, Session session)
        {
            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Script payload must be a JSON object");
            }

            if (!body.Value.TryGetProperty("script", out var scriptElement))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Script is required");
            }

            var script = scriptElement.GetString();
            if (string.IsNullOrEmpty(script))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Script cannot be empty");
            }

            var args = new List<object>();
            if (body.Value.TryGetProperty("args", out var argsElement))
            {
                if (argsElement.ValueKind != JsonValueKind.Array)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Script args must be an array");
                }

                foreach (var arg in argsElement.EnumerateArray())
                {
                    args.Add(DeserializeArg(arg, session));
                }
            }

            return (script, args.ToArray());
        }

        private object DeserializeArg(JsonElement element, Session session)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var whole) ? whole : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(item => DeserializeArg(item, session)).ToArray();
                case JsonValueKind.Object:
                    if (TryDeserializeReference(element, session, out var reference))
                        return reference;

                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        dict[property.Name] = DeserializeArg(property.Value, session);
                    }

                    return dict;
                default:
                    return null;
            }
        }

        private static bool TryDeserializeReference(JsonElement element, Session session, out object reference)
        {
            reference = null;
            var hasElement = element.TryGetProperty(ElementReference.Identifier, out var elementId);
            var hasShadow = element.TryGetProperty(ShadowRootReference.Identifier, out var shadowId);
            var hasFrame = element.TryGetProperty(FrameReference.Identifier, out var frameId);
            var hasWindow = element.TryGetProperty(WindowReference.Identifier, out var windowId);

            var referenceKeyCount =
                (hasElement ? 1 : 0) +
                (hasShadow ? 1 : 0) +
                (hasFrame ? 1 : 0) +
                (hasWindow ? 1 : 0);

            if (referenceKeyCount > 1)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Reference object cannot contain multiple WebDriver reference keys");
            }

            if (hasElement)
            {
                if (elementId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(elementId.GetString()))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Element reference value must be a non-empty string");
                }

                reference = session.GetElement(elementId.GetString(), Session.ElementReferenceKind.Element);
                return true;
            }

            if (hasShadow)
            {
                if (shadowId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(shadowId.GetString()))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Shadow root reference value must be a non-empty string");
                }

                reference = session.GetElement(shadowId.GetString(), Session.ElementReferenceKind.ShadowRoot);
                return true;
            }

            if (hasFrame)
            {
                if (frameId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(frameId.GetString()))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Frame reference value must be a non-empty string");
                }

                reference = session.GetElement(frameId.GetString(), Session.ElementReferenceKind.Frame);
                return true;
            }

            if (hasWindow)
            {
                if (windowId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(windowId.GetString()))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Window reference value must be a non-empty string");
                }

                reference = session.GetElement(windowId.GetString(), Session.ElementReferenceKind.Window);
                return true;
            }

            return false;
        }

        private object SerializeResult(object result, Session session)
        {
            if (result == null)
                return null;

            if (result is string token &&
                token.StartsWith(WebDriverElementTokenPrefix, StringComparison.Ordinal))
            {
                var nativeElementId = token.Substring(WebDriverElementTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeElementId, out var existingFromToken))
                {
                    return new ElementReference(existingFromToken);
                }

                return new ElementReference(session.RegisterElement(nativeElementId));
            }

            if (result is string shadowToken &&
                shadowToken.StartsWith(WebDriverShadowTokenPrefix, StringComparison.Ordinal))
            {
                var nativeShadowId = shadowToken.Substring(WebDriverShadowTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeShadowId, out var existingShadowRef))
                {
                    return new ShadowRootReference(existingShadowRef);
                }

                return new ShadowRootReference(session.RegisterShadowRoot(nativeShadowId));
            }

            if (result is string frameToken &&
                frameToken.StartsWith(WebDriverFrameTokenPrefix, StringComparison.Ordinal))
            {
                var nativeFrameId = frameToken.Substring(WebDriverFrameTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeFrameId, out var existingFrameRef))
                {
                    return new FrameReference(existingFrameRef);
                }

                return new FrameReference(session.RegisterFrame(nativeFrameId));
            }

            if (result is string windowToken &&
                windowToken.StartsWith(WebDriverWindowTokenPrefix, StringComparison.Ordinal))
            {
                var nativeWindowId = windowToken.Substring(WebDriverWindowTokenPrefix.Length);
                if (session.TryGetElementReferenceId(nativeWindowId, out var existingWindowRef))
                {
                    return new WindowReference(existingWindowRef);
                }

                return new WindowReference(session.RegisterWindow(nativeWindowId));
            }

            if (session.TryGetElementReferenceId(result, out var existingReference))
            {
                if (session.TryGetReferenceKind(existingReference, out var existingKind))
                {
                    return existingKind switch
                    {
                        Session.ElementReferenceKind.ShadowRoot => new ShadowRootReference(existingReference),
                        Session.ElementReferenceKind.Frame => new FrameReference(existingReference),
                        Session.ElementReferenceKind.Window => new WindowReference(existingReference),
                        _ => new ElementReference(existingReference)
                    };
                }

                return new ElementReference(existingReference);
            }

            if (IsElement(result))
            {
                return new ElementReference(session.RegisterElement(result));
            }

            if (result is IDictionary dictionary)
            {
                var serialized = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key)
                    {
                        serialized[key] = SerializeResult(entry.Value, session);
                    }
                }

                return serialized;
            }

            if (result is IEnumerable enumerable && result is not string)
            {
                var items = new List<object>();
                foreach (var item in enumerable)
                {
                    items.Add(SerializeResult(item, session));
                }

                return items;
            }

            if (IsJsonPrimitive(result))
            {
                return result;
            }

            // Never leak raw runtime wrapper objects into JSON serialization.
            // Unknown objects are returned as string form unless recognized as element references.
            return result.ToString();
        }

        private static bool IsJsonPrimitive(object value)
        {
            return value is string
                   or bool
                   or byte
                   or sbyte
                   or short
                   or ushort
                   or int
                   or uint
                   or long
                   or ulong
                   or float
                   or double
                   or decimal;
        }

        private static bool LooksLikeDomWrapper(Type type)
        {
            var name = type.Name;
            if (name.IndexOf("Element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("NodeWrapper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("ElementWrapper", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var ns = type.Namespace ?? string.Empty;
            return ns.IndexOf(".DOM", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   name.EndsWith("Wrapper", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsElement(object obj)
        {
            var type = obj.GetType();
            var typeName = type.Name;
            return typeName == "Element" ||
                   typeName.EndsWith("Element", StringComparison.Ordinal) ||
                   LooksLikeDomWrapper(type);
        }

    }
}
