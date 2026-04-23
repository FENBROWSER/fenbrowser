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

            if (!session.WindowStateInitialized)
            {
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

            if (string.IsNullOrWhiteSpace(previousCurrentHandle))
            {
                // Preserve an intentionally invalid current context (e.g. after close).
                session.CurrentWindowHandle = null;
                session.WindowStateInitialized = true;
                return;
            }

            if (session.WindowHandles.Contains(previousCurrentHandle))
            {
                session.CurrentWindowHandle = previousCurrentHandle;
                session.WindowStateInitialized = true;
                return;
            }

            // Preserve stale selection so subsequent commands surface no such window
            // until client explicitly switches to a valid context.
            session.CurrentWindowHandle = previousCurrentHandle;
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

            if (_handler.Browser != null)
            {
                await _handler.Browser.CloseWindowAsync();
                await SynchronizeWindowStateAsync(session);

                // Closing the selected context must invalidate current selection until client explicitly switches.
                if (!string.IsNullOrWhiteSpace(closedHandle) &&
                    !session.WindowHandles.Contains(closedHandle))
                {
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
            
            int? x = null, y = null, width = null, height = null;
            
            if (body.HasValue)
            {
                if (body.Value.TryGetProperty("x", out var xEl) && xEl.ValueKind == JsonValueKind.Number)
                    x = xEl.GetInt32();
                if (body.Value.TryGetProperty("y", out var yEl) && yEl.ValueKind == JsonValueKind.Number)
                    y = yEl.GetInt32();
                if (body.Value.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number)
                    width = wEl.GetInt32();
                if (body.Value.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number)
                    height = hEl.GetInt32();
            }
            
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
            var windowType = "tab";
            if (body.HasValue && body.Value.TryGetProperty("type", out var typeEl))
            {
                var requested = typeEl.GetString();
                if (string.Equals(requested, "window", StringComparison.OrdinalIgnoreCase))
                {
                    windowType = "window";
                }
            }

            if (_handler.Browser == null)
            {
                throw new WebDriverException(ErrorCodes.UnknownError, "Browser not connected");
            }

            var handle = Guid.NewGuid().ToString("N");
            var created = await _handler.Browser.NewWindowAsync(windowType);
            if (!string.IsNullOrWhiteSpace(created))
            {
                handle = created;
            }

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
            object frameReference = null;
            if (body.HasValue && body.Value.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
            {
                if (idEl.ValueKind == JsonValueKind.Object &&
                    idEl.TryGetProperty(ElementReference.Identifier, out var elementIdEl))
                {
                    var webDriverElementId = elementIdEl.GetString();
                    frameReference = session.GetElement(webDriverElementId);
                }
                else if (idEl.ValueKind == JsonValueKind.String)
                {
                    frameReference = idEl.GetString();
                }
                else if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var idx))
                {
                    frameReference = idx;
                }
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
