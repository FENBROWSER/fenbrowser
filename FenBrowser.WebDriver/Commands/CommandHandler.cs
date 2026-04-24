// =============================================================================
// CommandHandler.cs
// W3C WebDriver Command Execution (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §6 - Commands
//                 https://www.w3.org/TR/webdriver2/#commands
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Commands;
using FenBrowser.WebDriver.Security;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Executes WebDriver commands.
    /// </summary>
    public class CommandHandler
    {
        private static readonly HashSet<string> _implementedCommands = new(StringComparer.Ordinal)
        {
            "GetStatus",
            "NewSession",
            "DeleteSession",
            "GetTimeouts",
            "SetTimeouts",
            "NavigateTo",
            "GetCurrentUrl",
            "Back",
            "Forward",
            "Refresh",
            "GetTitle",
            "GetWindowHandle",
            "CloseWindow",
            "SwitchToWindow",
            "GetWindowHandles",
            "NewWindow",
            "SwitchToFrame",
            "SwitchToParentFrame",
            "GetWindowRect",
            "SetWindowRect",
            "MaximizeWindow",
            "MinimizeWindow",
            "FullscreenWindow",
            "FindElement",
            "FindElements",
            "FindElementFromElement",
            "FindElementsFromElement",
            "GetShadowRoot",
            "FindElementFromShadowRoot",
            "FindElementsFromShadowRoot",
            "GetActiveElement",
            "IsElementSelected",
            "GetElementText",
            "GetElementProperty",
            "GetElementCssValue",
            "GetElementTagName",
            "GetElementRect",
            "IsElementEnabled",
            "GetComputedRole",
            "GetComputedLabel",
            "ElementClick",
            "ElementClear",
            "ElementSendKeys",
            "GetElementAttribute",
            "GetPageSource",
            "ExecuteScript",
            "ExecuteAsyncScript",
            "GetAllCookies",
            "GetNamedCookie",
            "AddCookie",
            "DeleteCookie",
            "DeleteAllCookies",
            "PerformActions",
            "ReleaseActions",
            "DismissAlert",
            "AcceptAlert",
            "GetAlertText",
            "SendAlertText",
            "TakeScreenshot",
            "TakeElementScreenshot",
            "PrintPage"
        };
        private static readonly HashSet<string> _topLevelContextCommands = new(StringComparer.Ordinal)
        {
            "NavigateTo",
            "GetCurrentUrl",
            "Back",
            "Forward",
            "Refresh",
            "GetTitle",
            "GetWindowHandle",
            "CloseWindow",
            "GetWindowRect",
            "SetWindowRect",
            "MaximizeWindow",
            "MinimizeWindow",
            "FullscreenWindow",
            "SwitchToFrame",
            "SwitchToParentFrame",
            "FindElement",
            "FindElements",
            "FindElementFromElement",
            "FindElementsFromElement",
            "GetShadowRoot",
            "FindElementFromShadowRoot",
            "FindElementsFromShadowRoot",
            "GetActiveElement",
            "IsElementSelected",
            "GetElementText",
            "GetElementProperty",
            "GetElementCssValue",
            "GetElementTagName",
            "GetElementRect",
            "IsElementEnabled",
            "GetComputedRole",
            "GetComputedLabel",
            "ElementClick",
            "ElementClear",
            "ElementSendKeys",
            "GetElementAttribute",
            "GetPageSource",
            "ExecuteScript",
            "ExecuteAsyncScript",
            "GetAllCookies",
            "GetNamedCookie",
            "AddCookie",
            "DeleteCookie",
            "DeleteAllCookies",
            "PerformActions",
            "ReleaseActions",
            "DismissAlert",
            "AcceptAlert",
            "GetAlertText",
            "SendAlertText",
            "TakeScreenshot",
            "TakeElementScreenshot",
            "PrintPage"
        };
        private static readonly HashSet<string> _sessionScopedCommands = new(StringComparer.Ordinal)
        {
            "DeleteSession",
            "GetTimeouts",
            "SetTimeouts"
        };

        private readonly SessionManager _sessionManager;
        private readonly SessionCommands _sessionCommands;
        private readonly NavigationCommands _navigationCommands;
        private readonly ElementCommands _elementCommands;
        private readonly ScriptCommands _scriptCommands;
        private readonly WindowCommands _windowCommands;
        private readonly ConcurrentDictionary<string, CapabilityGuard> _capabilityGuards = new();
        private readonly SandboxEnforcer _sandboxEnforcer;
        
        // Browser integration - set when browser is connected
        public IBrowserDriver Browser { get; set; }
        
        public CommandHandler(SessionManager sessionManager)
        {
            _sessionManager = sessionManager;
            _sessionCommands = new SessionCommands(sessionManager);
            _navigationCommands = new NavigationCommands(this);
            _elementCommands = new ElementCommands(this);
            _scriptCommands = new ScriptCommands(this);
            _windowCommands = new WindowCommands(this);
            _sandboxEnforcer = new SandboxEnforcer();
        }
        
        /// <summary>
        /// Execute a command.
        /// </summary>
        public async Task<WebDriverResponse> ExecuteAsync(RouteMatch match, string body)
        {
            var command = match.Command;
            Console.WriteLine($"[WD-Cmd] {command}");
            var sessionId = match.GetSessionId();

            // Ensure per-session security context exists for all session-scoped commands.
            if (!string.Equals(command, "GetStatus", StringComparison.Ordinal) &&
                !string.Equals(command, "NewSession", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(sessionId))
            {
                EnsureSessionSecurityContext(sessionId);
            }
            
            // Parse body as JSON if present
            JsonElement? json = null;
            if (!string.IsNullOrEmpty(body))
            {
                json = JsonSerializer.Deserialize<JsonElement>(body);
            }
            EnsureCommandPreconditions(command, sessionId);

            try
            {
                return command switch
                {
                // Status
                "GetStatus" => GetStatus(),
                 
                // Session
                "NewSession" => await CreateSessionAndInitializeTopLevelContextAsync(json),
                "DeleteSession" => DeleteSessionSecure(match.GetSessionId()),
                "GetTimeouts" => _sessionCommands.GetTimeouts(match.GetSessionId()),
                "SetTimeouts" => _sessionCommands.SetTimeouts(match.GetSessionId(), json),
                
                // Navigation
                "NavigateTo" => await _navigationCommands.NavigateToAsync(match.GetSessionId(), json),
                "GetCurrentUrl" => await _navigationCommands.GetCurrentUrlAsync(match.GetSessionId()),
                "Back" => await _navigationCommands.BackAsync(match.GetSessionId()),
                "Forward" => await _navigationCommands.ForwardAsync(match.GetSessionId()),
                "Refresh" => await _navigationCommands.RefreshAsync(match.GetSessionId()),
                "GetTitle" => await _navigationCommands.GetTitleAsync(match.GetSessionId()),
                
                // Window
                "GetWindowHandle" => await _windowCommands.GetWindowHandleAsync(match.GetSessionId()),
                "CloseWindow" => await CloseWindowWithSessionLifecycleAsync(match.GetSessionId()),
                "SwitchToWindow" => await _windowCommands.SwitchToWindowAsync(match.GetSessionId(), json),
                "GetWindowHandles" => await _windowCommands.GetWindowHandlesAsync(match.GetSessionId()),
                "NewWindow" => await _windowCommands.NewWindowAsync(match.GetSessionId(), json),
                "SwitchToFrame" => await _windowCommands.SwitchToFrameAsync(match.GetSessionId(), json),
                "SwitchToParentFrame" => await _windowCommands.SwitchToParentFrameAsync(match.GetSessionId()),
                "GetWindowRect" => await _windowCommands.GetWindowRectAsync(match.GetSessionId()),
                "SetWindowRect" => await _windowCommands.SetWindowRectAsync(match.GetSessionId(), json),
                "MaximizeWindow" => await _windowCommands.MaximizeWindowAsync(match.GetSessionId()),
                "MinimizeWindow" => await _windowCommands.MinimizeWindowAsync(match.GetSessionId()),
                "FullscreenWindow" => await _windowCommands.FullscreenWindowAsync(match.GetSessionId()),
                
                // Elements
                "FindElement" => await _elementCommands.FindElementAsync(match.GetSessionId(), json),
                "FindElements" => await _elementCommands.FindElementsAsync(match.GetSessionId(), json),
                "FindElementFromElement" => await _elementCommands.FindElementFromElementAsync(match.GetSessionId(), match.GetElementId(), json),
                "FindElementsFromElement" => await _elementCommands.FindElementsFromElementAsync(match.GetSessionId(), match.GetElementId(), json),
                "GetShadowRoot" => await _elementCommands.GetShadowRootAsync(match.GetSessionId(), match.GetElementId()),
                "FindElementFromShadowRoot" => await _elementCommands.FindElementFromShadowRootAsync(match.GetSessionId(), match.GetShadowId(), json),
                "FindElementsFromShadowRoot" => await _elementCommands.FindElementsFromShadowRootAsync(match.GetSessionId(), match.GetShadowId(), json),
                "GetActiveElement" => await _elementCommands.GetActiveElementAsync(match.GetSessionId()),
                "IsElementSelected" => await _elementCommands.IsSelectedAsync(match.GetSessionId(), match.GetElementId()),
                "GetElementText" => await _elementCommands.GetElementTextAsync(match.GetSessionId(), match.GetElementId()),
                "GetElementProperty" => await _elementCommands.GetPropertyAsync(match.GetSessionId(), match.GetElementId(), match.Parameters.GetValueOrDefault("name")),
                "GetElementCssValue" => await _elementCommands.GetCssValueAsync(match.GetSessionId(), match.GetElementId(), match.Parameters.GetValueOrDefault("propertyName")),
                "GetElementTagName" => await _elementCommands.GetTagNameAsync(match.GetSessionId(), match.GetElementId()),
                "GetElementRect" => await _elementCommands.GetRectAsync(match.GetSessionId(), match.GetElementId()),
                "IsElementEnabled" => await _elementCommands.IsEnabledAsync(match.GetSessionId(), match.GetElementId()),
                "GetComputedRole" => await _elementCommands.GetComputedRoleAsync(match.GetSessionId(), match.GetElementId()),
                "GetComputedLabel" => await _elementCommands.GetComputedLabelAsync(match.GetSessionId(), match.GetElementId()),
                "ElementClick" => await _elementCommands.ClickAsync(match.GetSessionId(), match.GetElementId()),
                "ElementClear" => await _elementCommands.ClearAsync(match.GetSessionId(), match.GetElementId()),
                "ElementSendKeys" => await _elementCommands.SendKeysAsync(match.GetSessionId(), match.GetElementId(), json),
                "GetElementAttribute" => await _elementCommands.GetAttributeAsync(match.GetSessionId(), match.GetElementId(), match.Parameters.GetValueOrDefault("name")),
                
                // Document
                "GetPageSource" => await GetPageSourceAsync(match.GetSessionId()),

                // Scripts
                "ExecuteScript" => await _scriptCommands.ExecuteSyncAsync(match.GetSessionId(), json),
                "ExecuteAsyncScript" => await _scriptCommands.ExecuteAsyncAsync(match.GetSessionId(), json),

                // Cookies
                "GetAllCookies" => await GetAllCookiesAsync(match.GetSessionId()),
                "GetNamedCookie" => await GetNamedCookieAsync(match.GetSessionId(), match.Parameters.GetValueOrDefault("name")),
                "AddCookie" => await AddCookieAsync(match.GetSessionId(), json),
                "DeleteCookie" => await DeleteCookieAsync(match.GetSessionId(), match.Parameters.GetValueOrDefault("name")),
                "DeleteAllCookies" => await DeleteAllCookiesAsync(match.GetSessionId()),

                // Actions
                "PerformActions" => await PerformActionsAsync(match.GetSessionId(), json),
                "ReleaseActions" => await ReleaseActionsAsync(match.GetSessionId()),

                // Alerts
                "DismissAlert" => await DismissAlertAsync(match.GetSessionId()),
                "AcceptAlert" => await AcceptAlertAsync(match.GetSessionId()),
                "GetAlertText" => await GetAlertTextAsync(match.GetSessionId()),
                "SendAlertText" => await SendAlertTextAsync(match.GetSessionId(), json),
                
                // Screenshot
                "TakeScreenshot" => await TakeScreenshotAsync(match.GetSessionId()),
                "TakeElementScreenshot" => await _elementCommands.TakeElementScreenshotAsync(match.GetSessionId(), match.GetElementId()),
                
                // Print
                "PrintPage" => await PrintPageAsync(match.GetSessionId(), json),
                
                // Not implemented - return unsupported
                _ => WebDriverResponse.Error(ErrorCodes.UnsupportedOperation, $"Command not implemented: {command}")
                };
            }
            catch (WebDriverException)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                throw new WebDriverException(ErrorCodes.Timeout, ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, ex.Message);
            }
            catch (FormatException ex)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, ex.Message);
            }
            catch (NotSupportedException ex)
            {
                throw new WebDriverException(ErrorCodes.UnsupportedOperation, ex.Message);
            }
            catch (InvalidOperationException ex) when (LooksLikeNoSuchWindow(ex))
            {
                throw new WebDriverException(ErrorCodes.NoSuchWindow, "Current browsing context is no longer open");
            }
            catch (InvalidOperationException ex) when (LooksLikeStaleElement(ex))
            {
                throw new WebDriverException(ErrorCodes.StaleElementReference, "Element is no longer attached to the DOM");
            }
            catch (InvalidOperationException ex) when (LooksLikeNoSuchElement(ex))
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, ex.Message);
            }
        }
        
        private WebDriverResponse GetStatus()
        {
            return WebDriverResponse.Success(new
            {
                ready = true,
                message = "FenBrowser WebDriver ready"
            });
        }
        
        private async Task<WebDriverResponse> TakeScreenshotAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            
            if (Browser == null)
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            
            var base64 = await Browser.TakeScreenshotAsync();
            return WebDriverResponse.Success(base64);
        }

        private async Task<WebDriverResponse> GetPageSourceAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            var source = await Browser.GetPageSourceAsync();
            return WebDriverResponse.Success(source ?? string.Empty);
        }

        private async Task<WebDriverResponse> GetAllCookiesAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            EnsureSessionStorageIsolationForCookieCommands(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            var cookies = await Browser.GetAllCookiesAsync();
            return WebDriverResponse.Success(cookies ?? Array.Empty<WdCookie>());
        }

        private async Task<WebDriverResponse> GetNamedCookieAsync(string sessionId, string name)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            EnsureSessionStorageIsolationForCookieCommands(sessionId);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cookie name is required");
            }

            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            var cookie = await Browser.GetNamedCookieAsync(name);
            if (cookie == null)
            {
                throw new WebDriverException(ErrorCodes.NoSuchElement, $"Cookie not found: {name}");
            }

            return WebDriverResponse.Success(cookie);
        }

        private async Task<WebDriverResponse> AddCookieAsync(string sessionId, JsonElement? json)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            EnsureSessionStorageIsolationForCookieCommands(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (!json.HasValue || !json.Value.TryGetProperty("cookie", out var cookieEl) || cookieEl.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cookie payload is required");
            }

            var cookie = JsonSerializer.Deserialize<WdCookie>(cookieEl.GetRawText()) ?? new WdCookie();
            if (string.IsNullOrWhiteSpace(cookie.Name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cookie name is required");
            }

            await Browser.AddCookieAsync(cookie);
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> DeleteCookieAsync(string sessionId, string name)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            EnsureSessionStorageIsolationForCookieCommands(sessionId);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Cookie name is required");
            }

            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            await Browser.DeleteCookieAsync(name);
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> DeleteAllCookiesAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            EnsureSessionStorageIsolationForCookieCommands(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            await Browser.DeleteAllCookiesAsync();
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> PerformActionsAsync(string sessionId, JsonElement? json)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (!json.HasValue || !json.Value.TryGetProperty("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Actions payload is required");
            }

            var actions = new List<WdActionSequence>();
            var sequenceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seqEl in actionsEl.EnumerateArray())
            {
                if (seqEl.ValueKind != JsonValueKind.Object) continue;
                var sequence = new WdActionSequence();
                if (!seqEl.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Action sequence type is required");
                }

                sequence.Type = typeEl.GetString() ?? string.Empty;
                if (!seqEl.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Action sequence id is required");
                }

                sequence.Id = idEl.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sequence.Id))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Action sequence id cannot be empty");
                }
                if (!sequenceIds.Add(sequence.Id))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Duplicate action sequence id: {sequence.Id}");
                }

                if (!seqEl.TryGetProperty("actions", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Action sequence actions must be an array");
                }

                foreach (var itemEl in itemsEl.EnumerateArray())
                {
                    if (itemEl.ValueKind != JsonValueKind.Object)
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, "Action entry must be an object");
                    }

                    var item = new WdActionItem();
                    if (!itemEl.TryGetProperty("type", out var itType) || itType.ValueKind != JsonValueKind.String)
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, "Action type is required");
                    }

                    item.Type = itType.GetString() ?? string.Empty;
                    if (itemEl.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                    {
                        if (!dur.TryGetInt32(out var d) || d < 0)
                        {
                            throw new WebDriverException(ErrorCodes.InvalidArgument, "Action duration must be a non-negative integer");
                        }

                        item.Duration = d;
                    }

                    if (itemEl.TryGetProperty("x", out var xEl) && xEl.ValueKind == JsonValueKind.Number && xEl.TryGetInt32(out var x)) item.X = x;
                    if (itemEl.TryGetProperty("y", out var yEl) && yEl.ValueKind == JsonValueKind.Number && yEl.TryGetInt32(out var y)) item.Y = y;
                    if (itemEl.TryGetProperty("button", out var bEl) && bEl.ValueKind == JsonValueKind.Number && bEl.TryGetInt32(out var b)) item.Button = b;
                    if (itemEl.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.String) item.Value = vEl.GetString() ?? string.Empty;
                    if (itemEl.TryGetProperty("origin", out var originEl))
                    {
                        if (originEl.ValueKind == JsonValueKind.String)
                        {
                            item.Origin = originEl.GetString() ?? string.Empty;
                        }
                        else if (originEl.ValueKind == JsonValueKind.Object &&
                                 originEl.TryGetProperty(ElementReference.Identifier, out var originIdEl))
                        {
                            item.Origin = originIdEl.GetString() ?? string.Empty;
                        }
                    }

                    ValidateActionItem(sequence.Type, item);
                    sequence.Actions.Add(item);
                }

                if (sequence.Actions.Count == 0)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Action sequence '{sequence.Id}' must include at least one action");
                }
                actions.Add(sequence);
            }

            await Browser.PerformActionsAsync(actions);
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> ReleaseActionsAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            await Browser.ReleaseActionsAsync();
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> DismissAlertAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (!await Browser.HasAlertAsync())
            {
                throw new WebDriverException(ErrorCodes.NoSuchAlert, "No alert is open");
            }

            await Browser.DismissAlertAsync();
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> AcceptAlertAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (!await Browser.HasAlertAsync())
            {
                throw new WebDriverException(ErrorCodes.NoSuchAlert, "No alert is open");
            }

            await Browser.AcceptAlertAsync();
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> GetAlertTextAsync(string sessionId)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (!await Browser.HasAlertAsync())
            {
                throw new WebDriverException(ErrorCodes.NoSuchAlert, "No alert is open");
            }

            var text = await Browser.GetAlertTextAsync();
            return WebDriverResponse.Success(text ?? string.Empty);
        }

        private async Task<WebDriverResponse> SendAlertTextAsync(string sessionId, JsonElement? json)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (!await Browser.HasAlertAsync())
            {
                throw new WebDriverException(ErrorCodes.NoSuchAlert, "No alert is open");
            }

            if (!json.HasValue || !json.Value.TryGetProperty("text", out var textEl))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Alert text is required");
            }

            await Browser.SendAlertTextAsync(textEl.GetString() ?? string.Empty);
            return WebDriverResponse.Success(null);
        }

        private async Task<WebDriverResponse> PrintPageAsync(string sessionId, JsonElement? json)
        {
            EnsureTopLevelBrowsingContext(sessionId);
            if (Browser == null)
            {
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            }

            var options = json.HasValue
                ? JsonSerializer.Deserialize<WdPrintOptions>(json.Value.GetRawText()) ?? new WdPrintOptions()
                : new WdPrintOptions();

            var base64 = await Browser.PrintPageAsync(options);
            return WebDriverResponse.Success(base64 ?? string.Empty);
        }

        private WebDriverResponse RegisterSessionSecurityContext(WebDriverResponse response)
        {
            if (response?.Value is NewSessionResponse ns && !string.IsNullOrEmpty(ns.SessionId))
            {
                EnsureSessionSecurityContext(ns.SessionId);
            }
            return response;
        }

        private async Task<WebDriverResponse> CreateSessionAndInitializeTopLevelContextAsync(JsonElement? json)
        {
            var response = RegisterSessionSecurityContext(_sessionCommands.NewSession(json));
            if (response?.Value is not NewSessionResponse created || string.IsNullOrWhiteSpace(created.SessionId))
            {
                return response;
            }

            if (Browser == null)
            {
                return response;
            }

            var session = _sessionManager.GetSession(created.SessionId);
            if (session == null)
            {
                return response;
            }

            // Session isolation: provision a dedicated top-level context instead of attaching to pre-existing global handles.
            var dedicatedHandle = await Browser.NewWindowAsync("tab").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(dedicatedHandle))
            {
                var current = await Browser.GetWindowHandleAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(current))
                {
                    dedicatedHandle = current;
                }
            }

            session.WindowHandles.Clear();
            if (!string.IsNullOrWhiteSpace(dedicatedHandle))
            {
                session.WindowHandles.Add(dedicatedHandle);
                session.CurrentWindowHandle = dedicatedHandle;
                await Browser.SwitchToWindowAsync(dedicatedHandle).ConfigureAwait(false);
            }
            session.WindowStateInitialized = true;
            return response;
        }

        private async Task<WebDriverResponse> CloseWindowWithSessionLifecycleAsync(string sessionId)
        {
            var response = await _windowCommands.CloseWindowAsync(sessionId);
            var remainingHandles = (response?.Value as System.Collections.IEnumerable)?
                .Cast<object>()
                .Select(v => v?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList() ?? new List<string>();

            // Session lifecycle is tied to session-owned top-level contexts.
            var shouldDeleteSession = remainingHandles.Count == 0;

            if (shouldDeleteSession)
            {
                _sessionCommands.DeleteSession(sessionId);
                _capabilityGuards.TryRemove(sessionId, out _);
                _sandboxEnforcer.DestroySandbox(sessionId);
            }

            return response;
        }

        private WebDriverResponse DeleteSessionSecure(string sessionId)
        {
            try
            {
                return _sessionCommands.DeleteSession(sessionId);
            }
            finally
            {
                if (!string.IsNullOrEmpty(sessionId))
                {
                    _capabilityGuards.TryRemove(sessionId, out _);
                    _sandboxEnforcer.DestroySandbox(sessionId);
                }
            }
        }

        private void EnsureSessionSecurityContext(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            var session = _sessionManager.GetSession(sessionId);
            _capabilityGuards.GetOrAdd(sessionId, _ => new CapabilityGuard(session));
            if (_sandboxEnforcer.GetSandbox(sessionId) == null)
            {
                _sandboxEnforcer.CreateSandbox(sessionId);
            }
        }

        public bool IsNavigationAllowed(string sessionId, string url)
        {
            EnsureSessionSecurityContext(sessionId);
            if (!_capabilityGuards.TryGetValue(sessionId, out var guard))
            {
                return false;
            }

            return guard.IsUrlAllowed(url);
        }

        public bool IsScriptAllowed(string sessionId, string script)
        {
            EnsureSessionSecurityContext(sessionId);
            if (!_capabilityGuards.TryGetValue(sessionId, out var guard))
            {
                return false;
            }

            return guard.IsScriptAllowed(script);
        }

        public void EnsureNavigationAllowed(string sessionId, string url)
        {
            EnsureSessionSecurityContext(sessionId);
            if (!_capabilityGuards.TryGetValue(sessionId, out var guard))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Session capability policy unavailable");
            }

            var decision = guard.EvaluateUrlPolicy(url);
            if (decision.Allowed)
            {
                return;
            }

            SecurityAudit.LogBlocked(decision.ReasonCode, decision.Detail, sessionId);
            throw new WebDriverException(
                ErrorCodes.InvalidArgument,
                SecurityAudit.BuildBlockedMessage(decision.ReasonCode),
                SecurityAudit.CreateFailureData(decision.ReasonCode, decision.Detail, sessionId));
        }

        public void EnsureScriptAllowed(string sessionId, string script)
        {
            EnsureSessionSecurityContext(sessionId);
            if (!_capabilityGuards.TryGetValue(sessionId, out var guard))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Session capability policy unavailable");
            }

            var decision = guard.EvaluateScriptPolicy(script);
            if (decision.Allowed)
            {
                return;
            }

            SecurityAudit.LogBlocked(decision.ReasonCode, decision.Detail, sessionId);
            throw new WebDriverException(
                ErrorCodes.InvalidArgument,
                SecurityAudit.BuildBlockedMessage(decision.ReasonCode),
                SecurityAudit.CreateFailureData(decision.ReasonCode, decision.Detail, sessionId));
        }

        public void EnsureTopLevelBrowsingContext(string sessionId)
        {
            var session = _sessionManager.GetSession(sessionId);
            if (session == null)
            {
                throw new WebDriverException(ErrorCodes.InvalidSessionId, "Session is required");
            }

            if (string.IsNullOrWhiteSpace(session.CurrentWindowHandle))
            {
                throw new WebDriverException(ErrorCodes.NoSuchWindow, "No top-level browsing context is currently selected");
            }

            if (session.WindowHandles == null || !session.WindowHandles.Contains(session.CurrentWindowHandle))
            {
                throw new WebDriverException(
                    ErrorCodes.NoSuchWindow,
                    $"Current top-level browsing context is not open: {session.CurrentWindowHandle}");
            }

            if (Browser != null && !Browser.HasValidCurrentBrowsingContext())
            {
                throw new WebDriverException(
                    ErrorCodes.NoSuchWindow,
                    "Current browsing context is no longer open");
            }
        }

        public static IReadOnlyCollection<string> GetImplementedCommands() => _implementedCommands;
        
        public Session GetSession(string sessionId) => _sessionManager.GetSession(sessionId);

        private void EnsureCommandPreconditions(string command, string sessionId)
        {
            if (command is "GetStatus" or "NewSession")
            {
                return;
            }

            if (_sessionScopedCommands.Contains(command) || _topLevelContextCommands.Contains(command) || command is "SwitchToWindow" or "NewWindow" or "GetWindowHandles")
            {
                _sessionManager.GetSession(sessionId);
            }

            if (_topLevelContextCommands.Contains(command))
            {
                EnsureTopLevelBrowsingContext(sessionId);
            }
        }

        private static bool LooksLikeNoSuchWindow(InvalidOperationException ex)
        {
            return ex.Message.IndexOf("browsing context", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ex.Message.IndexOf("window handle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ex.Message.IndexOf("active tab", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeStaleElement(InvalidOperationException ex)
        {
            return ex.Message.IndexOf("stale element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ex.Message.IndexOf("detached", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeNoSuchElement(InvalidOperationException ex)
        {
            return ex.Message.IndexOf("no such element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ex.Message.IndexOf("element not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   ex.Message.IndexOf("invalid element reference", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void EnsureSessionStorageIsolationForCookieCommands(string sessionId)
        {
            // Single-session workflows are allowed with existing host behavior.
            if (_sessionManager.ActiveSessionCount <= 1)
            {
                return;
            }

            if (Browser != null && Browser.SupportsSessionStorageIsolation())
            {
                return;
            }

            const string reasonCode = SecurityBlockReasons.SessionIsolationViolation;
            const string detail = "Cookie commands are blocked in multi-session mode unless the browser driver guarantees per-session storage isolation";
            SecurityAudit.LogBlocked(reasonCode, detail, sessionId);
            throw new WebDriverException(
                ErrorCodes.UnsupportedOperation,
                SecurityAudit.BuildBlockedMessage(reasonCode),
                SecurityAudit.CreateFailureData(reasonCode, detail, sessionId));
        }

        private static void ValidateActionItem(string sequenceType, WdActionItem item)
        {
            var sourceType = sequenceType?.Trim().ToLowerInvariant() ?? string.Empty;
            var actionType = item.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(actionType))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Action type cannot be empty");
            }

            switch (sourceType)
            {
                case "pointer":
                    if (actionType is not ("pause" or "pointermove" or "pointerdown" or "pointerup" or "pointercancel"))
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported pointer action type: {item.Type}");
                    }

                    if ((actionType == "pointerdown" || actionType == "pointerup") && (item.Button < 0 || item.Button > 4))
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, $"Pointer button must be in [0,4], got {item.Button}");
                    }
                    break;
                case "key":
                    if (actionType is not ("pause" or "keydown" or "keyup"))
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported key action type: {item.Type}");
                    }
                    break;
                case "none":
                    if (actionType != "pause")
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, $"Source type 'none' only supports pause actions, got {item.Type}");
                    }
                    break;
                case "wheel":
                    if (actionType != "scroll")
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported wheel action type: {item.Type}");
                    }

                    throw new WebDriverException(ErrorCodes.UnsupportedOperation, "Wheel input source is not supported yet");
                default:
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported action source type: {sequenceType}");
            }
        }
    }
    
    /// <summary>
    /// Interface for browser integration.
    /// </summary>
    public interface IBrowserDriver
    {
        Task NavigateAsync(string url);
        Task<string> GetCurrentUrlAsync();
        Task<string> GetTitleAsync();
        Task<string> GetWindowHandleAsync();
        Task<IReadOnlyList<string>> GetWindowHandlesAsync();
        Task CloseWindowAsync();
        Task GoBackAsync();
        Task GoForwardAsync();
        Task RefreshAsync();
        
        Task<object> FindElementAsync(string strategy, string selector, object parentElement = null);
        Task<object[]> FindElementsAsync(string strategy, string selector, object parentElement = null);
        Task<object> GetActiveElementAsync();
        Task<object> GetShadowRootAsync(object element);
        Task<bool> IsElementSelectedAsync(object element);
        Task<object> GetElementPropertyAsync(object element, string name);
        Task<string> GetElementCssValueAsync(object element, string propertyName);
        Task<string> GetElementTextAsync(object element);
        Task<string> GetElementTagNameAsync(object element);
        Task<WdElementRect> GetElementRectAsync(object element);
        Task<bool> IsElementEnabledAsync(object element);
        Task<string> GetElementComputedRoleAsync(object element);
        Task<string> GetElementComputedLabelAsync(object element);
        Task ClickElementAsync(object element);
        Task ClearElementAsync(object element);
        Task SendKeysAsync(object element, string text);
        Task<string> GetElementAttributeAsync(object element, string name);
        
        Task<string> GetPageSourceAsync();
        Task<object> ExecuteScriptAsync(string script, object[] args);
        Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout);
        
        Task<string> TakeScreenshotAsync();
        Task<string> TakeElementScreenshotAsync(object element);
        Task<string> PrintPageAsync(WdPrintOptions options);
        
        (int x, int y, int width, int height) GetWindowRect();
        void SetWindowRect(int? x, int? y, int? width, int? height);
        (int x, int y, int width, int height) MaximizeWindow();
        (int x, int y, int width, int height) MinimizeWindow();
        (int x, int y, int width, int height) FullscreenWindow();
        Task<string> NewWindowAsync(string typeHint);
        Task SwitchToWindowAsync(string windowHandle);
        Task SwitchToFrameAsync(object frameReference);
        Task SwitchToParentFrameAsync();

        Task<IReadOnlyList<WdCookie>> GetAllCookiesAsync();
        Task<WdCookie> GetNamedCookieAsync(string name);
        Task AddCookieAsync(WdCookie cookie);
        Task DeleteCookieAsync(string name);
        Task DeleteAllCookiesAsync();

        Task PerformActionsAsync(IReadOnlyList<WdActionSequence> actions);
        Task ReleaseActionsAsync();

        Task<bool> HasAlertAsync();
        Task DismissAlertAsync();
        Task AcceptAlertAsync();
        Task<string> GetAlertTextAsync();
        Task SendAlertTextAsync(string text);
        bool HasValidCurrentBrowsingContext();
        bool SupportsSessionStorageIsolation() => false;
    }
}
