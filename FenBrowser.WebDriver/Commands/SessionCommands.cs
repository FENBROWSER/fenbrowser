using System;
using System.Collections.Generic;
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

                try
                {
                    ValidateNewSessionPayload(body.Value);
                    requestedCaps = CreateRequestedCapabilities(body.Value);
                }
                catch (JsonException ex)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Invalid capabilities payload: {ex.Message}");
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
            if (session.Capabilities.WebSocketUrl is bool webSocketRequested && webSocketRequested)
            {
                session.Capabilities.WebSocketUrl = $"ws://127.0.0.1/session/{session.Id}";
            }

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

        private static void ValidateNewSessionPayload(JsonElement body)
        {
            if (!body.TryGetProperty("capabilities", out var capabilities) ||
                capabilities.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "capabilities must be a JSON object");
            }

            if (capabilities.TryGetProperty("alwaysMatch", out var alwaysMatch))
            {
                if (alwaysMatch.ValueKind != JsonValueKind.Object)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "capabilities.alwaysMatch must be a JSON object");
                }

                ValidateCapabilityObject(alwaysMatch);
            }

            if (capabilities.TryGetProperty("firstMatch", out var firstMatch))
            {
                if (firstMatch.ValueKind != JsonValueKind.Array)
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "capabilities.firstMatch must be a JSON array");
                }

                foreach (var entry in firstMatch.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, "capabilities.firstMatch entries must be JSON objects");
                    }

                    ValidateCapabilityObject(entry);
                }
            }
        }

        private static Capabilities CreateRequestedCapabilities(JsonElement body)
        {
            var capabilities = body.GetProperty("capabilities");
            var alwaysMatch = capabilities.TryGetProperty("alwaysMatch", out var always)
                ? always
                : default;

            if (capabilities.TryGetProperty("firstMatch", out var firstMatch))
            {
                foreach (var first in firstMatch.EnumerateArray())
                {
                    RejectDuplicateCapabilityNames(alwaysMatch, first);
                }

                foreach (var first in firstMatch.EnumerateArray())
                {
                    var merged = MergeCapabilityObjects(alwaysMatch, first);
                    if (CapabilityMatchesImplementation(merged))
                    {
                        return DeserializeCapabilities(merged);
                    }
                }

                throw new WebDriverException(ErrorCodes.SessionNotCreated, "No matching capabilities found");
            }

            return DeserializeCapabilities(alwaysMatch.ValueKind == JsonValueKind.Object ? alwaysMatch.GetRawText() : "{}");
        }

        private static void RejectDuplicateCapabilityNames(JsonElement alwaysMatch, JsonElement firstMatch)
        {
            if (alwaysMatch.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var names = new HashSet<string>(alwaysMatch.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
            foreach (var property in firstMatch.EnumerateObject())
            {
                if (names.Contains(property.Name))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Capability {property.Name} is present in alwaysMatch and firstMatch");
                }
            }
        }

        private static string MergeCapabilityObjects(JsonElement alwaysMatch, JsonElement firstMatch)
        {
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (alwaysMatch.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in alwaysMatch.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }
                }

                foreach (var property in firstMatch.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static bool CapabilityMatchesImplementation(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("browserName", out var browserName) &&
                browserName.ValueKind == JsonValueKind.String &&
                !string.Equals(browserName.GetString(), "FenBrowser", StringComparison.Ordinal))
            {
                return false;
            }

            if (root.TryGetProperty("platformName", out var platformName) &&
                platformName.ValueKind == JsonValueKind.String &&
                !string.Equals(platformName.GetString(), "windows", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static Capabilities DeserializeCapabilities(string json)
        {
            return JsonSerializer.Deserialize<Capabilities>(json) ?? new Capabilities();
        }

        private static void ValidateCapabilityObject(JsonElement capabilities)
        {
            foreach (var property in capabilities.EnumerateObject())
            {
                ValidateCapabilityProperty(property.Name, property.Value);
            }
        }

        private static void ValidateCapabilityProperty(string name, JsonElement value)
        {
            switch (name)
            {
                case "acceptInsecureCerts":
                case "strictFileInteractability":
                    RequireBooleanOrNull(name, value);
                    break;
                case "browserName":
                case "browserVersion":
                case "platformName":
                    RequireStringOrNull(name, value);
                    break;
                case "pageLoadStrategy":
                    ValidatePageLoadStrategy(value);
                    break;
                case "proxy":
                    ValidateProxy(value);
                    break;
                case "timeouts":
                    ValidateTimeouts(value);
                    break;
                case "unhandledPromptBehavior":
                    ValidateUnhandledPromptBehavior(value);
                    break;
                case "webSocketUrl":
                    ValidateWebSocketUrl(value);
                    break;
                case "setWindowRect":
                case "userAgent":
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} is not a settable capability");
                default:
                    if (!name.Contains(':', StringComparison.Ordinal))
                    {
                        throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported capability: {name}");
                    }
                    break;
            }
        }

        private static void RequireBooleanOrNull(string name, JsonElement value)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} must be a boolean or null");
            }
        }

        private static void RequireStringOrNull(string name, JsonElement value)
        {
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} must be a string or null");
            }
        }

        private static void ValidatePageLoadStrategy(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "pageLoadStrategy must be a string or null");
            }

            if (value.GetString() is not ("none" or "eager" or "normal"))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "pageLoadStrategy is unsupported");
            }
        }

        private static void ValidateProxy(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "proxy must be a JSON object or null");
            }

            if (!value.TryGetProperty("proxyType", out var proxyType) ||
                proxyType.ValueKind != JsonValueKind.String ||
                proxyType.GetString() is not ("direct" or "manual" or "pac" or "autodetect" or "system"))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "proxy.proxyType is required and must be supported");
            }
        }

        private static void ValidateTimeouts(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "timeouts must be a JSON object or null");
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "script", "pageLoad", "implicit" };
            foreach (var property in value.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, $"Unsupported timeout: {property.Name}");
                }

                ValidateTimeoutValue(property.Name, property.Value);
            }
        }

        private static void ValidateTimeoutValue(string name, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var rawValue) ||
                double.IsNaN(rawValue) ||
                double.IsInfinity(rawValue) ||
                rawValue < 0 ||
                rawValue >= 9007199254740992d ||
                Math.Truncate(rawValue) != rawValue)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout must be a non-negative integer or null");
            }
        }

        private static void ValidateUnhandledPromptBehavior(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                if (value.GetString() is not ("dismiss" or "accept" or "dismiss and notify" or "accept and notify" or "ignore"))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "unhandledPromptBehavior is unsupported");
                }

                return;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "unhandledPromptBehavior is unsupported");
            }

            var allowedPrompts = new HashSet<string>(StringComparer.Ordinal)
            {
                "alert",
                "beforeUnload",
                "confirm",
                "default",
                "prompt"
            };

            foreach (var property in value.EnumerateObject())
            {
                if (!allowedPrompts.Contains(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    property.Value.GetString() is not ("dismiss" or "accept" or "dismiss and notify" or "accept and notify" or "ignore"))
                {
                    throw new WebDriverException(ErrorCodes.InvalidArgument, "unhandledPromptBehavior is unsupported");
                }
            }
        }

        private static void ValidateWebSocketUrl(JsonElement value)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, "webSocketUrl must be a boolean or null");
            }
        }

        private static long? ParseTimeout(string name, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Null)
                return null;

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var rawValue))
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout must be a non-negative integer or null");
            }

            if (double.IsNaN(rawValue) || double.IsInfinity(rawValue) || rawValue < 0 || rawValue >= 9007199254740992d)
            {
                throw new WebDriverException(ErrorCodes.InvalidArgument, $"{name} timeout must stay within [0, 2^53 - 1]");
            }

            return (long)Math.Floor(rawValue);
        }
    }
}
