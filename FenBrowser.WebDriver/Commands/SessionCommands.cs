// =============================================================================
// SessionCommands.cs
// W3C WebDriver Session Commands
// 
// SPEC REFERENCE: W3C WebDriver §8 - Sessions
//                 https://www.w3.org/TR/webdriver2/#sessions
// =============================================================================

using System.Text.Json;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Commands
{
    /// <summary>
    /// Session management commands.
    /// </summary>
    public class SessionCommands
    {
        private readonly SessionManager _sessionManager;
        
        public SessionCommands(SessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }
        
        /// <summary>
        /// Create a new session.
        /// POST /session
        /// </summary>
        public WebDriverResponse NewSession(JsonElement? body)
        {
            Capabilities requestedCaps = null;
            
            if (body.HasValue)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<CapabilityRequest>(body.Value.GetRawText());
                    requestedCaps = request?.Capabilities?.AlwaysMatch;
                    
                    // If alwaysMatch is null, try firstMatch
                    if (requestedCaps == null && request?.Capabilities?.FirstMatch?.Count > 0)
                    {
                        requestedCaps = request.Capabilities.FirstMatch[0];
                    }
                }
                catch
                {
                    // Ignore parse errors, use defaults
                }
            }
            
            var session = _sessionManager.CreateSession(requestedCaps);
            
            return WebDriverResponse.Success(new NewSessionResponse
            {
                SessionId = session.Id,
                Capabilities = session.Capabilities
            });
        }
        
        /// <summary>
        /// Delete a session.
        /// DELETE /session/{sessionId}
        /// </summary>
        public WebDriverResponse DeleteSession(string sessionId)
        {
            _sessionManager.GetSession(sessionId); // Validate exists
            _sessionManager.DeleteSession(sessionId);
            return WebDriverResponse.Success(null);
        }
        
        /// <summary>
        /// Get session timeouts.
        /// GET /session/{sessionId}/timeouts
        /// </summary>
        public WebDriverResponse GetTimeouts(string sessionId)
        {
            var session = _sessionManager.GetSession(sessionId);
            return WebDriverResponse.Success(session.Timeouts);
        }
        
        /// <summary>
        /// Set session timeouts.
        /// POST /session/{sessionId}/timeouts
        /// </summary>
        public WebDriverResponse SetTimeouts(string sessionId, JsonElement? body)
        {
            var session = _sessionManager.GetSession(sessionId);
            
            if (body.HasValue)
            {
                if (body.Value.TryGetProperty("script", out var script) && script.ValueKind == JsonValueKind.Number)
                    session.Timeouts.Script = script.GetInt32();
                    
                if (body.Value.TryGetProperty("pageLoad", out var pageLoad) && pageLoad.ValueKind == JsonValueKind.Number)
                    session.Timeouts.PageLoad = pageLoad.GetInt32();
                    
                if (body.Value.TryGetProperty("implicit", out var impl) && impl.ValueKind == JsonValueKind.Number)
                    session.Timeouts.Implicit = impl.GetInt32();
            }
            
            return WebDriverResponse.Success(null);
        }
    }
}
