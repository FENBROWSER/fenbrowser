// =============================================================================
// WindowCommands.cs
// W3C WebDriver Window Commands
// 
// SPEC REFERENCE: W3C WebDriver §11 - Contexts
//                 https://www.w3.org/TR/webdriver2/#contexts
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Security;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Window management commands.
    /// </summary>
    public class WindowCommands
    {
        private readonly CommandHandler _handler;
        
        public WindowCommands(CommandHandler handler)
        {
            _handler = handler;
        }

        private async Task SynchronizeWindowStateAsync(Session session)
        {
            if (_handler.IsSessionUnresponsive(session.Id))
            {
                return;
            }

            if (_handler.Browser == null)
            {
                return;
            }

            var previousCurrentHandle = session.CurrentWindowHandle;
            var browserHandles = (await _handler.Browser.GetWindowHandlesAsync())
                ?.Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

            // Keep only handles owned by this session and still open in browser.
            session.WindowHandles.RemoveAll(handle => !browserHandles.Contains(handle));

            // Single-session workflows mirror host windows into the one session.
            // Multi-session sessions must never observe or adopt another session's
            // dedicated contexts; merging foreign handles here would let
            // SwitchToWindow accept them as if they were owned by this session.
            if (_handler.ActiveSessionCount <= 1)
            {
                foreach (var handle in browserHandles)
                {
                    if (!session.WindowHandles.Contains(handle))
                    {
                        session.WindowHandles.Add(handle);
                    }
                }
            }

            if (!session.WindowStateInitialized)
            {
                if (_handler.ActiveSessionCount > 1)
                {
                    // Fail closed: a raw multi-session construction has no safe handle
                    // to adopt, so context commands keep surfacing `no such window`
                    // until the client explicitly selects an owned handle.
                    session.WindowStateInitialized = true;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(previousCurrentHandle) && browserHandles.Contains(previousCurrentHandle))
                {
                    if (!session.WindowHandles.Contains(previousCurrentHandle))
                    {
                        session.WindowHandles.Add(previousCurrentHandle);
                    }
                }
                else
                {
                    var bootstrapCurrent = await _handler.Browser.GetWindowHandleAsync();
                    if (!string.IsNullOrWhiteSpace(bootstrapCurrent) && browserHandles.Contains(bootstrapCurrent))
                    {
                        if (!session.WindowHandles.Contains(bootstrapCurrent))
                        {
                            session.WindowHandles.Add(bootstrapCurrent);
                        }

                        session.CurrentWindowHandle = bootstrapCurrent;
                    }
                }

                session.WindowStateInitialized = true;
                return;
            }

            if (!string.IsNullOrWhiteSpace(previousCurrentHandle) &&
                session.WindowHandles.Contains(previousCurrentHandle))
            {
                session.CurrentWindowHandle = previousCurrentHandle;
                session.WindowStateInitialized = true;
                return;
            }

            // Preserve the selected handle even when it is now closed.
            // WebDriver commands that require a current top-level context must then fail
            // with "no such window" until the client explicitly selects another handle.
            if (!string.IsNullOrWhiteSpace(previousCurrentHandle))
            {
                session.CurrentWindowHandle = previousCurrentHandle;
                session.WindowStateInitialized = true;
                return;
            }

            session.CurrentWindowHandle = session.WindowHandles.FirstOrDefault();
            session.WindowStateInitialized = true;
            return;
        }
        
        /// <summary>
        /// Get current window handle.
        /// GET /session/{sessionId}/window
        /// </summary>
        public async Task<WebDriverResponse> GetWindowHandleAsync(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
            await SynchronizeWindowStateAsync(session);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            if (string.IsNullOrWhiteSpace(session.CurrentWindowHandle))
            {
                throw new WebDriverException(ErrorCodes.NoSuchWindow, "No top-level browsing context is currently selected");
            }

            return WebDriverResponse.Success(session.CurrentWindowHandle);
        }
        
        /// <summary>
        /// Close current window.
        /// DELETE /session/{sessionId}/window
        /// </summary>
        public async Task<WebDriverResponse> CloseWindowAsync(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
            await SynchronizeWindowStateAsync(session);

            if (string.IsNullOrWhiteSpace(session.CurrentWindowHandle) ||
                !session.WindowHandles.Contains(session.CurrentWindowHandle))
            {
                throw new WebDriverException(ErrorCodes.NoSuchWindow, "No top-level browsing context is currently selected");
            }

            var closedHandle = session.CurrentWindowHandle;

            if (_handler.IsSessionUnresponsive(sessionId))
            {
                session.WindowHandles.Remove(closedHandle);
                session.CurrentWindowHandle = session.WindowHandles.FirstOrDefault();
                return WebDriverResponse.Success(session.WindowHandles);
            }

            if (_handler.Browser != null)
            {
                // Multi-session safety: ensure browser-side active context matches the session-selected handle.
                await _handler.Browser.SwitchToWindowAsync(closedHandle);
                await _handler.Browser.CloseWindowAsync();
                await SynchronizeWindowStateAsync(session);

                if (!string.IsNullOrWhiteSpace(closedHandle) &&
                    !session.WindowHandles.Contains(closedHandle))
                {
                    // Keep the now-closed handle selected until client switches explicitly.
                    session.CurrentWindowHandle = closedHandle;
                }
            }
            else
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            
            return WebDriverResponse.Success(session.WindowHandles);
        }
        
        /// <summary>
        /// Get all window handles.
        /// GET /session/{sessionId}/window/handles
        /// </summary>
        public async Task<WebDriverResponse> GetWindowHandlesAsync(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
            await SynchronizeWindowStateAsync(session);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            return WebDriverResponse.Success(session.WindowHandles);
        }
        
        /// <summary>
        /// Get window rect.
        /// GET /session/{sessionId}/window/rect
        /// </summary>
        public async Task<WebDriverResponse> GetWindowRectAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            
            var (x, y, width, height) = _handler.Browser.GetWindowRect();
            return WebDriverResponse.Success(new WindowRect
            {
                X = x, Y = y, Width = width, Height = height
            });
        }
        
        /// <summary>
        /// Set window rect.
        /// POST /session/{sessionId}/window/rect
        /// </summary>
        public async Task<WebDriverResponse> SetWindowRectAsync(string sessionId, JsonElement? body)
        {
            _handler.GetSession(sessionId);
            
            // WebDriver 11.8.2 Set Window Rect: each member is null/absent or a Number
            // in range (width and height 0..2^31-1, x and y any 32-bit integer);
            // anything else is an invalid argument, not a silently ignored value.
            var x = ReadRectMember(body, "x", int.MinValue);
            var y = ReadRectMember(body, "y", int.MinValue);
            var width = ReadRectMember(body, "width", 0);
            var height = ReadRectMember(body, "height", 0);
            
            if (_handler.Browser != null)
            {
                _handler.Browser.SetWindowRect(x, y, width, height);
            }
            else
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            
            return await GetWindowRectAsync(sessionId);
        }

        public async Task<WebDriverResponse> SwitchToWindowAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            await SynchronizeWindowStateAsync(session);
            if (!body.HasValue || !body.Value.TryGetProperty("handle", out var handleEl))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Window handle is required");
            }

            var handle = handleEl.GetString();
            if (string.IsNullOrWhiteSpace(handle))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Window handle cannot be empty");
            }

            if (!session.WindowHandles.Contains(handle))
            {
                if (_handler.Browser != null)
                {
                    var browserHandles = await _handler.Browser.GetWindowHandlesAsync();
                    if (browserHandles != null && browserHandles.Contains(handle, StringComparer.Ordinal))
                    {
                        const string reasonCode = SecurityBlockReasons.SessionIsolationViolation;
                        const string detail = "Attempted to switch to a window handle not owned by this session";
                        SecurityAudit.LogBlocked(reasonCode, $"{detail}: handle={handle}", sessionId);
                        throw new WebDriverException(
                            ErrorCodes.NoSuchWindow,
                            $"No such window: {handle}",
                            SecurityAudit.CreateFailureData(reasonCode, detail, sessionId));
                    }
                }

                throw new WebDriverException(ErrorCodes.NoSuchWindow, $"No such window: {handle}");
            }

            session.CurrentWindowHandle = handle;
            if (_handler.IsSessionUnresponsive(sessionId))
            {
                return WebDriverResponse.Success(null);
            }

            if (_handler.Browser != null)
            {
                await _handler.Browser.SwitchToWindowAsync(handle);
                await SynchronizeWindowStateAsync(session);
            }

            return WebDriverResponse.Success(null);
        }

        public async Task<WebDriverResponse> NewWindowAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            await SynchronizeWindowStateAsync(session);
            var previousHandle = session.CurrentWindowHandle;

            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "New window parameters must be an object");
            }

            if (string.IsNullOrWhiteSpace(previousHandle) ||
                session.WindowHandles == null ||
                !session.WindowHandles.Contains(previousHandle))
            {
                throw new WebDriverException(ErrorCodes.NoSuchWindow, "No top-level browsing context is currently selected");
            }

            var windowType = "tab";
            if (body.Value.TryGetProperty("type", out var typeEl))
            {
                if (typeEl.ValueKind == JsonValueKind.Null)
                {
                    windowType = "tab";
                }
                else if (typeEl.ValueKind != JsonValueKind.String)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "New window type must be a string or null");
                }
                else
                {
                    var requested = typeEl.GetString();
                    if (string.Equals(requested, "window", StringComparison.OrdinalIgnoreCase))
                    {
                        windowType = "window";
                    }
                }
            }

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            // The type is only a hint, and this browser opens every new top-level
            // context as a tab: report what was created, not what was asked for.
            var handle = await _handler.Browser.NewWindowAsync(windowType);
            if (string.IsNullOrWhiteSpace(handle))
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "The browser did not create a new top-level browsing context");
            }

            windowType = "tab";

            if (!session.WindowHandles.Contains(handle))
            {
                session.WindowHandles.Add(handle);
            }

            // Classic WebDriver keeps the current top-level browsing context unchanged
            // after creating a new one.
            if (!string.IsNullOrWhiteSpace(previousHandle))
            {
                session.CurrentWindowHandle = previousHandle;
                if (session.WindowHandles.Contains(previousHandle))
                {
                    await _handler.Browser.SwitchToWindowAsync(previousHandle);
                }
            }
            else
            {
                // If current context is already invalid, select the newly created one.
                session.CurrentWindowHandle = handle;
                await _handler.Browser.SwitchToWindowAsync(handle);
            }

            // Keep explicit session ownership for the new handle.
            await SynchronizeWindowStateAsync(session);
            if (!session.WindowHandles.Contains(handle))
            {
                session.WindowHandles.Add(handle);
            }

            return WebDriverResponse.Success(new { handle, type = windowType });
        }

        public async Task<WebDriverResponse> SwitchToFrameAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            // WebDriver 11.5 Switch To Frame: id is null (the top-level context), an
            // integer 0..65535 (a child frame index) or a web element reference.
            // Anything else - a missing id, a string, a shadow root - is an invalid
            // argument; it used to mean "switch to the top level".
            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object ||
                !body.Value.TryGetProperty("id", out var idEl))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Switch To Frame requires an id");
            }

            object frameReference = null;
            switch (idEl.ValueKind)
            {
                case JsonValueKind.Null:
                    break;
                case JsonValueKind.Number:
                    if (!idEl.TryGetDouble(out var number) || number != Math.Floor(number) || number < 0 || number > 65535)
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, "Frame index must be an integer from 0 to 65535");
                    }
                    frameReference = (int)number;
                    break;
                case JsonValueKind.Object when idEl.TryGetProperty(ElementReference.Identifier, out var elementIdEl) &&
                                               elementIdEl.ValueKind == JsonValueKind.String:
                    frameReference = session.GetElement(elementIdEl.GetString());
                    break;
                default:
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Frame id must be null, a number or a web element reference");
            }

            if (_handler.Browser != null)
            {
                await _handler.Browser.SwitchToFrameAsync(frameReference);
            }
            else
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            return WebDriverResponse.Success(null);
        }

        private static int? ReadRectMember(JsonElement? body, string name, long minimum)
        {
            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object ||
                !body.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
                double.IsNaN(number) || number < minimum || number > int.MaxValue)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"'{name}' must be null or a number from {minimum} to {int.MaxValue}");
            }

            return (int)Math.Floor(number);
        }

        public async Task<WebDriverResponse> SwitchToParentFrameAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser != null)
            {
                await _handler.Browser.SwitchToParentFrameAsync();
            }
            else
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }
            return WebDriverResponse.Success(null);
        }

        public async Task<WebDriverResponse> MaximizeWindowAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var (x, y, width, height) = _handler.Browser.MaximizeWindow();
            return WebDriverResponse.Success(new WindowRect { X = x, Y = y, Width = width, Height = height });
        }

        public async Task<WebDriverResponse> MinimizeWindowAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var (x, y, width, height) = _handler.Browser.MinimizeWindow();
            return WebDriverResponse.Success(new WindowRect { X = x, Y = y, Width = width, Height = height });
        }

        public async Task<WebDriverResponse> FullscreenWindowAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var (x, y, width, height) = _handler.Browser.FullscreenWindow();
            return WebDriverResponse.Success(new WindowRect { X = x, Y = y, Width = width, Height = height });
        }
    }
}
