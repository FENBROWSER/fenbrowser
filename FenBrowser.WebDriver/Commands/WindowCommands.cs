// =============================================================================
// WindowCommands.cs
// W3C WebDriver Window Commands
// 
// SPEC REFERENCE: W3C WebDriver §11 - Contexts
//                 https://www.w3.org/TR/webdriver2/#contexts
// =============================================================================

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
    }
}
