using System;
using System.Linq;
using System.Text.Json;
using FenBrowser.WebDriver.Protocol;
using FenBrowser.WebDriver.Security;

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
                if (body.Value.ValueKind != JsonValueKind.Object)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "Session payload must be a JSON object");
                }

                CapabilityRequest request;
                try
                {
                    request = JsonSerializer.Deserialize<CapabilityRequest>(body.Value.GetRawText());
                }
                catch (JsonException ex)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Invalid capabilities payload: {ex.Message}");
                }

                requestedCaps = request?.Capabilities?.AlwaysMatch;
                if (requestedCaps == null && request?.Capabilities?.FirstMatch?.Count > 0)
                {
                    requestedCaps = request.Capabilities.FirstMatch.FirstOrDefault();
                }
            }

            var securityDecision = CapabilityGuard.ValidateRequestedCapabilities(requestedCaps);
            if (!securityDecision.Allowed)
            {
                SecurityAudit.LogBlocked(securityDecision.ReasonCode, securityDecision.Detail);
                throw new WebDriverException(
                    ErrorCodes.InvalidArgument,
                    SecurityAudit.BuildBlockedMessage(securityDecision.ReasonCode),
                    SecurityAudit.CreateFailureData(securityDecision.ReasonCode, securityDecision.Detail));
            }

            requestedCaps?.ValidateOrThrow();
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
            _sessionManager.GetSession(sessionId);
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

            if (!body.HasValue || body.Value.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "Timeout payload must be a JSON object");
            }

            if (body.Value.TryGetProperty("script", out var script))
                session.Timeouts.Script = ParseTimeout("script", script);

            if (body.Value.TryGetProperty("pageLoad", out var pageLoad))
                session.Timeouts.PageLoad = ParseTimeout("pageLoad", pageLoad);

            if (body.Value.TryGetProperty("implicit", out var implicitTimeout))
                session.Timeouts.Implicit = ParseTimeout("implicit", implicitTimeout);

            session.Timeouts.ValidateOrThrow();
            return WebDriverResponse.Success(null);
        }

        private static int? ParseTimeout(string name, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var rawValue))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout must be a non-negative integer or null");
            }

            if (double.IsNaN(rawValue) || double.IsInfinity(rawValue) || rawValue < 0 || rawValue > int.MaxValue)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout must stay within [0, {int.MaxValue}]");
            }

            return (int)Math.Floor(rawValue);
        }
    }
}
