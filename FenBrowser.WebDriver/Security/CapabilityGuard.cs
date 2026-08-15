// =============================================================================
// CapabilityGuard.cs
// WebDriver Security - Capability Enforcement
//
// PURPOSE: Enforces capability restrictions at real browser/driver boundaries.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.WebDriver.Protocol;

namespace FenBrowser.WebDriver.Security
{
    /// <summary>
    /// Enforces security restrictions based on session capabilities.
    /// </summary>
    public class CapabilityGuard
    {
        private readonly Session _session;
        private static readonly HashSet<string> RiskyCapabilityArgs = new(StringComparer.OrdinalIgnoreCase)
        {
            "--allow-file-access",
            "--allow-insecure-localhost",
            "--disable-web-security"
        };
        private const string RiskyCapabilityOptIn = "--webdriver-allow-risky-capabilities";

        public CapabilityGuard(Session session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// Check if insecure certificates are allowed.
        /// </summary>
        public bool AllowInsecureCerts => _session.Capabilities.AcceptInsecureCerts == true;

        /// <summary>
        /// Check if a URL is allowed for navigation.
        /// </summary>
        public bool IsUrlAllowed(string url)
        {
            return EvaluateUrlPolicy(url).Allowed;
        }

        public SecurityDecision EvaluateUrlPolicy(string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Scheme))
            {
                return SecurityDecision.Block(SecurityBlockReasons.NavigationUrlInvalid, "Navigation URL is not an absolute URI");
            }

            switch (uri.Scheme.ToLowerInvariant())
            {
                case "file":
                    if (!AllowFileUrls())
                    {
                        return SecurityDecision.Block(
                            SecurityBlockReasons.NavigationUrlBlocked,
                            "file:// navigation requires explicit risky capability opt-in");
                    }
                    return SecurityDecision.Allow();

                case "javascript":
                    return SecurityDecision.Block(
                        SecurityBlockReasons.NavigationUrlBlocked,
                        "javascript: navigation is blocked");

                case "http":
                case "https":
                case "about":
                case "data":
                case "fen":
                    // Do not inspect data: payload text for words such as "script".
                    // Substring filtering both blocks harmless content and misses
                    // encoded executable content. Navigation/content security belongs
                    // to the browser's normal document policy pipeline.
                    return SecurityDecision.Allow();

                default:
                    return SecurityDecision.Block(
                        SecurityBlockReasons.NavigationUrlBlocked,
                        $"Navigation scheme '{uri.Scheme}' is not supported by this WebDriver remote end");
            }
        }

        private bool AllowFileUrls()
        {
            var args = _session.Capabilities.FenOptions?.Args;
            if (args == null || args.Count == 0)
                return false;

            // Capability negotiation validates this pair, but the operation boundary
            // must enforce it again. Sessions can be constructed by embedders/tests or
            // future protocol paths that accidentally bypass negotiation; a lone
            // --allow-file-access flag must never become sufficient by itself.
            var hasFileAccess = args.Any(arg =>
                string.Equals(arg, "--allow-file-access", StringComparison.OrdinalIgnoreCase));
            var hasRiskyOptIn = args.Any(arg =>
                string.Equals(arg, RiskyCapabilityOptIn, StringComparison.OrdinalIgnoreCase));
            return hasFileAccess && hasRiskyOptIn;
        }

        /// <summary>
        /// WebDriver Execute Script accepts a JavaScript function body. Security must
        /// be enforced by the browsing-context sandbox/capabilities, not by substring
        /// matching source text such as "process.exit" or "eval(atob(".
        /// </summary>
        public bool IsScriptAllowed(string script)
        {
            return EvaluateScriptPolicy(script).Allowed;
        }

        public SecurityDecision EvaluateScriptPolicy(string script)
        {
            if (script == null)
            {
                return SecurityDecision.Block(SecurityBlockReasons.ScriptBlocked, "Script source is null");
            }

            return SecurityDecision.Allow();
        }

        public int GetScriptTimeout()
        {
            return ToRuntimeTimeoutMs(_session.Timeouts.Script, 30000);
        }

        public int GetPageLoadTimeout()
        {
            return ToRuntimeTimeoutMs(_session.Timeouts.PageLoad, 300000);
        }

        private static int ToRuntimeTimeoutMs(long? configuredTimeoutMs, int fallbackMs)
        {
            if (!configuredTimeoutMs.HasValue)
            {
                return fallbackMs;
            }

            var value = configuredTimeoutMs.Value;
            if (value < 0)
            {
                return fallbackMs;
            }

            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        public static SecurityDecision ValidateRequestedCapabilities(Capabilities? caps)
        {
            // The current default WebDriver server installs NoOpBiDiTransportBootstrap,
            // while the concrete transport is a separately-owned listener whose
            // endpoint/lifecycle is not yet integrated with the HTTP remote end.
            // Never turn `webSocketUrl: true` into an advertised URL until the server
            // can actually start and own a standards-compliant BiDi listener.
            if (Capabilities.NormalizeWebSocketUrl(caps?.WebSocketUrl) is bool webSocketRequested && webSocketRequested)
            {
                return SecurityDecision.Block(
                    SecurityBlockReasons.CapabilityPolicyViolation,
                    "webSocketUrl/BiDi is unavailable until the WebDriver remote end owns a standards-compliant BiDi transport");
            }

            var args = caps?.FenOptions?.Args ?? new List<string>();
            if (args.Count == 0)
            {
                return SecurityDecision.Allow();
            }

            var hasRiskyOptIn = args.Any(arg => string.Equals(arg, RiskyCapabilityOptIn, StringComparison.OrdinalIgnoreCase));
            var riskyArgs = args.Where(IsRiskyCapabilityArgument).ToArray();
            if (riskyArgs.Length == 0)
            {
                return SecurityDecision.Allow();
            }

            if (!hasRiskyOptIn)
            {
                return SecurityDecision.Block(
                    SecurityBlockReasons.CapabilityPolicyViolation,
                    $"Risky capability arguments require explicit opt-in {RiskyCapabilityOptIn}: {string.Join(", ", riskyArgs)}");
            }

            return SecurityDecision.Allow();
        }

        private static bool IsRiskyCapabilityArgument(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
                return false;

            foreach (var risky in RiskyCapabilityArgs)
            {
                if (string.Equals(argument, risky, StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith(risky + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public readonly struct SecurityDecision
    {
        public bool Allowed { get; }
        public string ReasonCode { get; }
        public string Detail { get; }

        private SecurityDecision(bool allowed, string reasonCode, string detail)
        {
            Allowed = allowed;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public static SecurityDecision Allow() => new(true, string.Empty, string.Empty);

        public static SecurityDecision Block(string reasonCode, string detail) => new(false, reasonCode, detail);
    }
}
