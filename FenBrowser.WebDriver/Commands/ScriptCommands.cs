// =============================================================================
// ScriptCommands.cs
// W3C WebDriver Script Execution Commands
// 
// SPEC REFERENCE: W3C WebDriver §13 - Script Execution
//                 https://www.w3.org/TR/webdriver2/#executing-script
// =============================================================================

using System;
using System.Collections.Generic;
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
            var (script, args) = ParseScriptRequest(body);

            if (!_handler.IsScriptAllowed(sessionId, script))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Script blocked by security policy");
            }
            
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
            var (script, args) = ParseScriptRequest(body);

            if (!_handler.IsScriptAllowed(sessionId, script))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Script blocked by security policy");
            }
            
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
        
        private (string script, object[] args) ParseScriptRequest(JsonElement? body)
        {
            if (!body.HasValue)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Script is required");
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
            if (body.Value.TryGetProperty("args", out var argsElement) && 
                argsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in argsElement.EnumerateArray())
                {
                    args.Add(DeserializeArg(arg));
                }
            }
            
            return (script, args.ToArray());
        }
        
        private object DeserializeArg(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Array => DeserializeArray(element),
                JsonValueKind.Object => DeserializeObject(element),
                _ => null
            };
        }
        
        private object[] DeserializeArray(JsonElement element)
        {
            var list = new List<object>();
            foreach (var item in element.EnumerateArray())
            {
                list.Add(DeserializeArg(item));
            }
            return list.ToArray();
        }
        
        private Dictionary<string, object> DeserializeObject(JsonElement element)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = DeserializeArg(prop.Value);
            }
            return dict;
        }
        
        private object SerializeResult(object result, Session session)
        {
            // If result is an element, return element reference
            if (result != null && IsElement(result))
            {
                var elementId = session.RegisterElement(result);
                return new ElementReference(elementId);
            }
            
            return result;
        }
        
        private bool IsElement(object obj)
        {
            // Check if this is a DOM element (implementation-specific)
            var typeName = obj.GetType().Name;
            return typeName == "Element" || typeName.EndsWith("Element");
        }
    }
}
