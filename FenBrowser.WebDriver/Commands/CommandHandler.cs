// =============================================================================
// CommandHandler.cs
// W3C WebDriver Command Execution (Spec-Compliant)
// 
// SPEC REFERENCE: W3C WebDriver §6 - Commands
//                 https://www.w3.org/TR/webdriver2/#commands
// =============================================================================

using System;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Commands;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Executes WebDriver commands.
    /// </summary>
    public class CommandHandler
    {
        private readonly SessionManager _sessionManager;
        private readonly SessionCommands _sessionCommands;
        private readonly NavigationCommands _navigationCommands;
        private readonly ElementCommands _elementCommands;
        private readonly ScriptCommands _scriptCommands;
        private readonly WindowCommands _windowCommands;
        
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
        }
        
        /// <summary>
        /// Execute a command.
        /// </summary>
        public async Task<WebDriverResponse> ExecuteAsync(RouteMatch match, string body)
        {
            var command = match.Command;
            
            // Parse body as JSON if present
            JsonElement? json = null;
            if (!string.IsNullOrEmpty(body))
            {
                json = JsonSerializer.Deserialize<JsonElement>(body);
            }
            
            return command switch
            {
                // Status
                "GetStatus" => GetStatus(),
                
                // Session
                "NewSession" => _sessionCommands.NewSession(json),
                "DeleteSession" => _sessionCommands.DeleteSession(match.GetSessionId()),
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
                "GetWindowHandle" => _windowCommands.GetWindowHandle(match.GetSessionId()),
                "CloseWindow" => _windowCommands.CloseWindow(match.GetSessionId()),
                "GetWindowHandles" => _windowCommands.GetWindowHandles(match.GetSessionId()),
                "GetWindowRect" => await _windowCommands.GetWindowRectAsync(match.GetSessionId()),
                "SetWindowRect" => await _windowCommands.SetWindowRectAsync(match.GetSessionId(), json),
                
                // Elements
                "FindElement" => await _elementCommands.FindElementAsync(match.GetSessionId(), json),
                "FindElements" => await _elementCommands.FindElementsAsync(match.GetSessionId(), json),
                "GetElementText" => await _elementCommands.GetElementTextAsync(match.GetSessionId(), match.GetElementId()),
                "ElementClick" => await _elementCommands.ClickAsync(match.GetSessionId(), match.GetElementId()),
                "ElementSendKeys" => await _elementCommands.SendKeysAsync(match.GetSessionId(), match.GetElementId(), json),
                "GetElementAttribute" => await _elementCommands.GetAttributeAsync(match.GetSessionId(), match.GetElementId(), match.Parameters.GetValueOrDefault("name")),
                
                // Scripts
                "ExecuteScript" => await _scriptCommands.ExecuteSyncAsync(match.GetSessionId(), json),
                "ExecuteAsyncScript" => await _scriptCommands.ExecuteAsyncAsync(match.GetSessionId(), json),
                
                // Screenshot
                "TakeScreenshot" => await TakeScreenshotAsync(match.GetSessionId()),
                
                // Not implemented - return unsupported
                _ => WebDriverResponse.Error(ErrorCodes.UnsupportedOperation, $"Command not implemented: {command}")
            };
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
            _sessionManager.GetSession(sessionId); // Validate session
            
            if (Browser == null)
                return WebDriverResponse.Error(ErrorCodes.UnknownError, "Browser not connected");
            
            var base64 = await Browser.TakeScreenshotAsync();
            return WebDriverResponse.Success(base64);
        }
        
        public Session GetSession(string sessionId) => _sessionManager.GetSession(sessionId);
    }
    
    /// <summary>
    /// Interface for browser integration.
    /// </summary>
    public interface IBrowserDriver
    {
        Task NavigateAsync(string url);
        Task<string> GetCurrentUrlAsync();
        Task<string> GetTitleAsync();
        Task GoBackAsync();
        Task GoForwardAsync();
        Task RefreshAsync();
        
        Task<object> FindElementAsync(string strategy, string selector);
        Task<object[]> FindElementsAsync(string strategy, string selector);
        Task<string> GetElementTextAsync(object element);
        Task ClickElementAsync(object element);
        Task SendKeysAsync(object element, string text);
        Task<string> GetElementAttributeAsync(object element, string name);
        
        Task<object> ExecuteScriptAsync(string script, object[] args);
        Task<object> ExecuteAsyncScriptAsync(string script, object[] args, int timeout);
        
        Task<string> TakeScreenshotAsync();
        
        (int x, int y, int width, int height) GetWindowRect();
        void SetWindowRect(int? x, int? y, int? width, int? height);
    }
}
