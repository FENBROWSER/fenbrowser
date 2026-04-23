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

            try
            {
                var result = await _handler.Browser.ExecuteScriptAsync(script, args);
                return WebDriverResponse.Success(SerializeResult(result, session));
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
            catch (Exception ex)
            {
                throw new WebDriverException(ErrorCodes.JavaScriptError, ex.Message);
            }
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
            if (element.TryGetProperty(ElementReference.Identifier, out var elementId))
            {
                reference = session.GetElement(elementId.GetString());
                return true;
            }

            if (element.TryGetProperty(ShadowRootReference.Identifier, out var shadowId))
            {
                reference = session.GetElement(shadowId.GetString());
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

            if (session.TryGetElementReferenceId(result, out var existingReference))
            {
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
