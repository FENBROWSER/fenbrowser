// =============================================================================
// WindowCommands.cs
// W3C WebDriver Window Commands
// 
// SPEC REFERENCE: W3C WebDriver §11 - Contexts
//                 https://www.w3.org/TR/webdriver2/#contexts
// =============================================================================

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;

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
        
        /// <summary>
        /// Get current window handle.
        /// GET /session/{sessionId}/window
        /// </summary>
        public WebDriverResponse GetWindowHandle(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
            return WebDriverResponse.Success(session.CurrentWindowHandle);
        }
        
        /// <summary>
        /// Close current window.
        /// DELETE /session/{sessionId}/window
        /// </summary>
        public WebDriverResponse CloseWindow(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
            
            if (session.WindowHandles.Count > 0)
            {
                session.WindowHandles.Remove(session.CurrentWindowHandle);
            }
            
            if (session.WindowHandles.Count > 0)
            {
                session.CurrentWindowHandle = session.WindowHandles[0];
            }
            else
            {
                session.CurrentWindowHandle = null;
            }
            
            return WebDriverResponse.Success(session.WindowHandles);
        }
        
        /// <summary>
        /// Get all window handles.
        /// GET /session/{sessionId}/window/handles
        /// </summary>
        public WebDriverResponse GetWindowHandles(string sessionId)
        {
            var session = _handler.GetSession(sessionId);
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
                return WebDriverResponse.Success(new WindowRect
                {
                    X = 0, Y = 0, Width = 1920, Height = 1080
                });
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
            
            return await GetWindowRectAsync(sessionId);
        }

        public async Task<WebDriverResponse> SwitchToWindowAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
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
                throw new WebDriverException(ErrorCodes.NoSuchWindow, $"No such window: {handle}");
            }

            session.CurrentWindowHandle = handle;
            if (_handler.Browser != null)
            {
                await _handler.Browser.SwitchToWindowAsync(handle);
            }

            return WebDriverResponse.Success(null);
        }

        public async Task<WebDriverResponse> NewWindowAsync(string sessionId, JsonElement? body)
        {
            var session = _handler.GetSession(sessionId);
            var windowType = "tab";
            if (body.HasValue && body.Value.TryGetProperty("type", out var typeEl))
            {
                var requested = typeEl.GetString();
                if (string.Equals(requested, "window", StringComparison.OrdinalIgnoreCase))
                {
                    windowType = "window";
                }
            }

            var handle = Guid.NewGuid().ToString("N");
            if (_handler.Browser != null)
            {
                var created = await _handler.Browser.NewWindowAsync(windowType);
                if (!string.IsNullOrWhiteSpace(created))
                {
                    handle = created;
                }
            }

            if (!session.WindowHandles.Contains(handle))
            {
                session.WindowHandles.Add(handle);
            }
            session.CurrentWindowHandle = handle;

            return WebDriverResponse.Success(new { handle, type = windowType });
        }

        public async Task<WebDriverResponse> SwitchToFrameAsync(string sessionId, JsonElement? body)
        {
            _handler.GetSession(sessionId);
            object frameReference = null;
            if (body.HasValue && body.Value.TryGetProperty("id", out var idEl) && idEl.ValueKind != JsonValueKind.Null)
            {
                if (idEl.ValueKind == JsonValueKind.Object &&
                    idEl.TryGetProperty(ElementReference.Identifier, out var elementIdEl))
                {
                    frameReference = elementIdEl.GetString();
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
            return WebDriverResponse.Success(null);
        }

        public async Task<WebDriverResponse> SwitchToParentFrameAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser != null)
            {
                await _handler.Browser.SwitchToParentFrameAsync();
            }
            return WebDriverResponse.Success(null);
        }

        public async Task<WebDriverResponse> MaximizeWindowAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                return await GetWindowRectAsync(sessionId);
            }

            var (x, y, width, height) = _handler.Browser.MaximizeWindow();
            return WebDriverResponse.Success(new WindowRect { X = x, Y = y, Width = width, Height = height });
        }

        public async Task<WebDriverResponse> MinimizeWindowAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                return await GetWindowRectAsync(sessionId);
            }

            var (x, y, width, height) = _handler.Browser.MinimizeWindow();
            return WebDriverResponse.Success(new WindowRect { X = x, Y = y, Width = width, Height = height });
        }

        public async Task<WebDriverResponse> FullscreenWindowAsync(string sessionId)
        {
            _handler.GetSession(sessionId);
            if (_handler.Browser == null)
            {
                return await GetWindowRectAsync(sessionId);
            }

            var (x, y, width, height) = _handler.Browser.FullscreenWindow();
            return WebDriverResponse.Success(new WindowRect { X = x, Y = y, Width = width, Height = height });
        }
    }
}
